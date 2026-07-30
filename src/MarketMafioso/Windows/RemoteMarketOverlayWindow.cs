using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.UI.Tables;
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

    private static readonly DalamudTableProjection<RemoteMarketListingView> tableProjection = new(
    [
        new("Qty", 48f, listing => listing.Quantity.ToString(), listing => listing.Quantity),
        new("Unit", 84f, listing => listing.UnitPrice.ToString("N0"), listing => listing.UnitPrice, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort),
        new("Fee", 64f, listing => listing.TotalTax.ToString("N0"), listing => listing.TotalTax),
        new("Total", 104f, listing => listing.TotalGil.ToString("N0"), listing => listing.TotalGil),
        new("Mat", 36f, listing => listing.MateriaCount > 0 ? listing.MateriaCount.ToString() : string.Empty, listing => listing.MateriaCount),
        new("Retainer", 96f, listing => listing.RetainerName, listing => listing.RetainerName),
        new("State", 84f, listing => listing.AlreadyPurchased ? "purchased" : listing.BatchStatus?.ToString().ToLowerInvariant() ?? string.Empty),
    ]);

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

    public override bool DrawConditions() => controller.IsAvailable && controller.IsMarketBoardResultVisible();

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
        var batchActive = view.Batch is not null;

        if (view.Listings.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, view.BrowseMessage);
            return;
        }

        selectedListingIds.RemoveWhere(id => view.Listings.All(listing => listing.ListingId != id) ||
            view.Listings.First(listing => listing.ListingId == id).AlreadyPurchased);

        if (controller.ConsumePendingSelectionMaxPrice() is { } maxPrice)
        {
            selectedListingIds.Clear();
            foreach (var listing in view.Listings.Where(listing =>
                         !listing.AlreadyPurchased && (maxPrice == 0 || listing.UnitPrice <= maxPrice)))
                selectedListingIds.Add(listing.ListingId);
            confirmArmed = false;
        }

        DrawHeader(view);
        DrawEconomicsStrip(view);
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

        if (view.MarketContext is { } context)
            DrawMarketContextStrip(context, first.UnitPrice);

        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 34f);
        if (ImGui.SmallButton(pinned ? "Unpin" : "Pin"))
            pinned = !pinned;
    }

    private static void DrawMarketContextStrip(CmbMarketContext context, uint cheapestLocalUnitPrice)
    {
        var parts = new List<string>();
        if (context.DatacenterBestPrice is { } dcBest && !string.IsNullOrWhiteSpace(context.DatacenterBestWorld))
        {
            var delta = cheapestLocalUnitPrice > 0 ? ((double)cheapestLocalUnitPrice - dcBest) / cheapestLocalUnitPrice : 0;
            var deltaText = delta > 0.005 ? $" (-{delta:P0})" : delta < -0.005 ? $" (+{-delta:P0})" : string.Empty;
            parts.Add($"DC best {dcBest:N0}p ({context.DatacenterBestWorld}{deltaText})");
        }
        if (context.VelocityPerDay is { } velocity)
            parts.Add($"~{velocity:0.#}/day");
        if (context.TrendAveragePrice is { } trend)
            parts.Add($"sale avg {trend:0}p");
        if (parts.Count == 0)
            return;
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, string.Join(" · ", parts));
    }

    private static void DrawEconomicsStrip(RemoteMarketView view)
    {
        var buyable = view.Listings.Where(listing => !listing.AlreadyPurchased).ToArray();
        if (buyable.Length == 0)
            return;

        var cheapest = buyable.Min(listing => listing.UnitPrice);
        var sorted = buyable.Select(listing => listing.UnitPrice).OrderBy(price => price).ToArray();
        var median = sorted[sorted.Length / 2];
        var mean = buyable.Average(listing => (double)listing.UnitPrice);
        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            $"Cheapest {cheapest:N0}p · Median {median:N0}p · Mean {mean:N0}p");

        if (view.MarketContext?.TrendAveragePrice is not { } trend || trend <= 0)
            return;
        var delta = (cheapest - trend) / trend;
        if (Math.Abs(delta) < 0.005)
            return;
        ImGui.SameLine();
        ImGui.TextColored(
            delta > 0 ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted,
            delta > 0 ? $"▲ {delta:P0} above sale avg" : $"▼ {-delta:P0} below sale avg");
    }

    private void DrawListingsTable(RemoteMarketView view, bool batchActive)
    {
        var tableHeight = Math.Min(460f, Math.Max(140f, ImGui.GetContentRegionAvail().Y - 110f));
        if (!tableProjection.Begin("##RMCListingGrid", tableHeight))
            return;

        tableProjection.DrawFilterRow();
        foreach (var listing in tableProjection.Apply(view.Listings, ImGui.TableGetSortSpecs()))
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
            Cell(listing.RetainerName, muted);

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
        tableProjection.End();
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
