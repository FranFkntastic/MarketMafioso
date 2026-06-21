using Dalamud.Game.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MarketMafioso.Services;
using System;
using System.Linq;

namespace MarketMafioso.Windows;

public sealed class MasterWindow : Window
{
    private readonly IPluginLog _pluginLog;
    private readonly RetainerSnapshotStore _snapshotStore;
    private readonly RetainerMarketSnapshotStore _marketSnapshotStore;
    private readonly RetainerMarketCaptureService _retainerMarketCaptureService;
    private readonly PluginConfiguration _configuration;
    private readonly Action<bool> _setOverlayCollapsed;

    private string _statusMessage = "Idle";
    private ulong _selectedRetainerId;

    public MasterWindow(
        IPluginLog pluginLog,
        RetainerSnapshotStore snapshotStore,
        RetainerMarketSnapshotStore marketSnapshotStore,
        RetainerMarketCaptureService retainerMarketCaptureService,
        PluginConfiguration configuration,
        Action<bool> setOverlayCollapsed)
        : base("MarketMafioso###MmfMasterWindow")
    {
        _pluginLog = pluginLog;
        _snapshotStore = snapshotStore;
        _marketSnapshotStore = marketSnapshotStore;
        _retainerMarketCaptureService = retainerMarketCaptureService;
        _configuration = configuration;
        _setOverlayCollapsed = setOverlayCollapsed;

        IsOpen = false;
        Size = new System.Numerics.Vector2(880f, 560f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Open()
    {
        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.BeginDisabled(_retainerMarketCaptureService.IsRunning);
        if (ImGui.Button("Refresh Active + Market"))
        {
            StartMarketCaptureCycle();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Dump Latest"))
        {
            DumpLatestCapture();
        }

        var captureStatus = _retainerMarketCaptureService.StatusMessage;
        ImGui.TextDisabled(captureStatus == "Idle" ? _statusMessage : captureStatus);

        ImGui.Separator();

        DrawSettings();
        DrawRetainerCaptureData();
        DrawMarketCaptureData();
    }

    private void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var collapsed = _configuration.OverlayCollapsed;
        if (ImGui.Checkbox("Overlay starts collapsed", ref collapsed))
        {
            _configuration.OverlayCollapsed = collapsed;
            _configuration.Save();
            _setOverlayCollapsed(collapsed);
        }

        ImGui.TextDisabled("Use the overlay title-bar arrow for temporary collapse.");
    }

    private void DrawRetainerCaptureData()
    {
        if (!ImGui.CollapsingHeader("Captured Retainer Data", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var snapshots = _snapshotStore.GetAll();
        if (snapshots.Count == 0)
        {
            ImGui.TextUnformatted("No retainer captures yet.");
            return;
        }

        if (_selectedRetainerId == 0 || !_snapshotStore.TryGet(_selectedRetainerId, out _))
        {
            _selectedRetainerId = snapshots[0].RetainerId;
        }

        if (ImGui.BeginTable("MmfRetainerSnapshots", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Retainer");
            ImGui.TableSetupColumn("Listings");
            ImGui.TableSetupColumn("Captured");
            ImGui.TableHeadersRow();

            foreach (var snapshot in snapshots)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                var selected = snapshot.RetainerId == _selectedRetainerId;
                if (ImGui.Selectable($"{snapshot.RetainerName}##{snapshot.RetainerId}", selected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selectedRetainerId = snapshot.RetainerId;
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(snapshot.Listings.Count.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss"));
            }

            ImGui.EndTable();
        }

        if (!_snapshotStore.TryGet(_selectedRetainerId, out var selectedSnapshot))
        {
            return;
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Selected: {selectedSnapshot.RetainerName} ({selectedSnapshot.Listings.Count} listings)");

        if (selectedSnapshot.Listings.Count == 0)
        {
            ImGui.TextUnformatted("No listings captured.");
            return;
        }

        if (ImGui.BeginTable(
                "MmfRetainerListings",
                5,
                ImGuiTableFlags.Borders
                | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.Resizable
                | ImGuiTableFlags.SizingStretchProp
                | ImGuiTableFlags.Reorderable
                | ImGuiTableFlags.Hideable))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Price");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("Item Id", ImGuiTableColumnFlags.DefaultHide);
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.DefaultHide);
            ImGui.TableHeadersRow();

            foreach (var listing in selectedSnapshot.Listings)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                DrawItemName(listing);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(listing.Quantity.ToString());

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(listing.UnitPrice.ToString());

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(listing.ItemId.ToString());

                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(listing.Slot.ToString());
            }

            ImGui.EndTable();
        }
    }

    private static void DrawItemName(RetainerListingSnapshot listing)
    {
        if (listing.IsHq)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"{listing.ItemName} {SeIconChar.HighQuality.ToIconString()}");
            return;
        }

        ImGui.TextUnformatted(listing.ItemName);
    }

    private void DrawMarketCaptureData()
    {
        if (!ImGui.CollapsingHeader("Captured Market Data", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (_selectedRetainerId == 0)
        {
            ImGui.TextUnformatted("Select a retainer first.");
            return;
        }

        if (!_marketSnapshotStore.TryGet(_selectedRetainerId, out var marketSnapshot))
        {
            ImGui.TextUnformatted("No market snapshot captured for selected retainer.");
            return;
        }

        ImGui.TextUnformatted($"Captured: {marketSnapshot.CapturedAt:HH:mm:ss}");
        ImGui.TextUnformatted($"Items queried: {marketSnapshot.Items.Count}");

        if (marketSnapshot.Items.Count == 0)
        {
            ImGui.TextUnformatted("No listed items were available to query.");
            return;
        }

        if (ImGui.BeginTable("MmfItemMarketSummaries", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Listings");
            ImGui.TableSetupColumn("Lowest Price");
            ImGui.TableSetupColumn("Highest Price");
            ImGui.TableHeadersRow();

            foreach (var item in marketSnapshot.Items)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(item.ItemName);

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(item.Listings.Count.ToString());

                ImGui.TableSetColumnIndex(2);
                var lowestPrice = item.Listings.Count == 0 ? 0u : item.Listings.Min(listing => listing.UnitPrice);
                ImGui.TextUnformatted(lowestPrice.ToString());

                ImGui.TableSetColumnIndex(3);
                var highestPrice = item.Listings.Count == 0 ? 0u : item.Listings.Max(listing => listing.UnitPrice);
                ImGui.TextUnformatted(highestPrice.ToString());
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        foreach (var item in marketSnapshot.Items)
        {
            var lowestPrice = item.Listings.Count == 0 ? 0u : item.Listings.Min(listing => listing.UnitPrice);
            var highestPrice = item.Listings.Count == 0 ? 0u : item.Listings.Max(listing => listing.UnitPrice);
            if (!ImGui.CollapsingHeader(
                    $"{item.ItemName} ({item.Listings.Count} listings, low {lowestPrice}, high {highestPrice})##{item.ItemId}",
                    ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            if (item.Listings.Count == 0)
            {
                ImGui.TextUnformatted("Market response returned zero listings for this item.");
                continue;
            }

            if (ImGui.BeginTable(
                    $"MmfMarketListingDetails##{item.ItemId}",
                    7,
                    ImGuiTableFlags.Borders
                    | ImGuiTableFlags.RowBg
                    | ImGuiTableFlags.Resizable
                    | ImGuiTableFlags.SizingStretchProp
                    | ImGuiTableFlags.Reorderable
                    | ImGuiTableFlags.Hideable))
            {
                ImGui.TableSetupColumn("Retainer");
                ImGui.TableSetupColumn("Price");
                ImGui.TableSetupColumn("Qty");
                ImGui.TableSetupColumn("Quality");
                ImGui.TableSetupColumn("Retainer Id", ImGuiTableColumnFlags.DefaultHide);
                ImGui.TableSetupColumn("Listing Id", ImGuiTableColumnFlags.DefaultHide);
                ImGui.TableSetupColumn("Town", ImGuiTableColumnFlags.DefaultHide);
                ImGui.TableHeadersRow();

                foreach (var listing in item.Listings)
                {
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(listing.SellingRetainerName);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(listing.UnitPrice.ToString());

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(listing.Quantity.ToString());

                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(listing.IsHq ? "HQ" : "NQ");

                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextUnformatted(listing.SellingRetainerContentId.ToString());

                    ImGui.TableSetColumnIndex(5);
                    ImGui.TextUnformatted(listing.ListingId.ToString());

                    ImGui.TableSetColumnIndex(6);
                    ImGui.TextUnformatted(listing.TownId.ToString());
                }

                ImGui.EndTable();
            }
        }
    }

    private void StartMarketCaptureCycle()
    {
        try
        {
            _retainerMarketCaptureService.StartActiveRetainerCaptureCycle();
            if (_snapshotStore.TryGetLatest(out var snapshot))
            {
                _selectedRetainerId = snapshot.RetainerId;
            }

            _statusMessage = _retainerMarketCaptureService.StatusMessage;
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            _pluginLog.Error(ex, "[MarketMafioso] Failed to start market capture cycle.");
        }
    }

    private void DumpLatestCapture()
    {
        if (!_snapshotStore.TryGetLatest(out var snapshot))
        {
            _statusMessage = "No capture available.";
            return;
        }

        _pluginLog.Information($"[MarketMafioso] Capture: {snapshot.RetainerName} ({snapshot.RetainerId}), listings={snapshot.Listings.Count}, captured={snapshot.CapturedAt:O}");
        foreach (var listing in snapshot.Listings)
        {
            var hq = listing.IsHq ? "HQ" : "NQ";
            _pluginLog.Information($"[MarketMafioso] slot={listing.Slot} item={listing.ItemId} name=\"{listing.ItemName}\" {hq} qty={listing.Quantity} price={listing.UnitPrice}");
        }

        _statusMessage = $"Dumped {snapshot.Listings.Count} listings to log.";
    }
}
