using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketMafioso.MarketAcquisition.RemoteMarket;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

public sealed class RemoteMarketOverlayWindow : Window
{
    private readonly RemoteMarketController controller;
    private ulong? autoStagedListingId;

    internal RemoteMarketOverlayWindow(RemoteMarketController controller)
        : base(
            "##MarketMafiosoRemoteMarketOverlay",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
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
        AutoStageSelection(view);
        view = controller.GetView();

        if (view.Attempt is { } pending)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Header, $"{pending.Quantity}x {pending.ItemName}{(pending.IsHighQuality ? " (HQ)" : string.Empty)}");
            ImGui.Text($"{pending.TotalGil:N0} gil");
            if (pending.Phase == RemoteMarketPurchasePhase.AwaitingConfirmation)
            {
                if (ImGuiUi.Button("Confirm purchase", view.Available))
                    controller.ConfirmPurchase();
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    controller.CancelPurchase();
                    autoStagedListingId = null;
                }
            }
            else
            {
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, pending.Phase.ToString());
            }
        }
        else if (view.Selection is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Select a listing to buy remotely");
        }
    }

    private void AutoStageSelection(RemoteMarketView view)
    {
        if (!view.Available || view.Attempt is not null || view.Selection is not { } selection)
        {
            if (view.Selection is null)
                autoStagedListingId = null;
            return;
        }
        if (autoStagedListingId == selection.ListingId)
            return;
        if (controller.BeginPurchase() is null)
            autoStagedListingId = selection.ListingId;
    }
}
