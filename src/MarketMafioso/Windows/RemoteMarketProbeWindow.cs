using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketMafioso.MarketDiagnostics;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

public sealed class RemoteMarketProbeWindow : Window
{
    private readonly RemoteMarketAccessProbe probe;

    internal RemoteMarketProbeWindow(RemoteMarketAccessProbe probe)
        : base("MMF Remote Market Probe##MarketMafiosoRemoteMarketProbe")
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var view = probe.GetView();

        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Remote Market Probe");
        ImGui.Separator();
        DrawRow("Session", view.Armed ? "Armed" : "Not armed — /mmf probe-market");
        if (view.Verdict is not null)
            DrawRow("Verdict", $"{view.Verdict}: {view.VerdictReason}");
        DrawRow("Listings staged", view.ListingCount.ToString());
        DrawRow("Listings loading", view.WaitingForListings ? "Yes" : "No");

        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Selected Listing");
        if (view.SelectedIndex < 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Select a listing in the market board window.");
        }
        else
        {
            DrawRow("Row", (view.SelectedIndex + 1).ToString());
            DrawRow("Unit price", view.SelectedUnitPrice is { } price ? $"{price:N0} gil" : "unknown");
            DrawRow("Stack quantity", view.SelectedQuantity?.ToString() ?? "unknown");
            DrawRow("Tax (full stack)", view.SelectedTax is { } tax ? $"{tax:N0} gil" : "unknown");
            DrawRow("Quality", view.SelectedIsHq is true ? "HQ" : "NQ");
        }

        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Proxy Purchase");
        if (view.PurchaseRequestSent)
            DrawRow("Request", "Sent");
        if (view.PurchaseResponseReceived)
            DrawRow("Response", "Received");
        if (view.PurchaseBlockedReason is not null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Purchase selected (full stack) via proxy"))
        {
            var failure = probe.TryPurchaseSelected();
            if (failure is not null)
                Plugin.ChatGui.PrintError($"[MMF] Remote market probe purchase blocked: {failure}");
        }
        if (view.PurchaseBlockedReason is not null)
            ImGui.EndDisabled();
        if (view.PurchaseBlockedReason is not null)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, view.PurchaseBlockedReason);
        ImGui.TextWrapped("Stages the selected listing through InfoProxyItemSearch and sends the real purchase packet for the full stack, tax included. The game UI's own purchase button is bypassed entirely.");
    }

    private static void DrawRow(string label, string value)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{label}:");
        ImGui.SameLine(150f);
        ImGui.TextWrapped(value);
    }
}
