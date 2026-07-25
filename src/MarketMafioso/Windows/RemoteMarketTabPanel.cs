using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketAcquisition.RemoteMarket;
using MarketMafioso.MarketDiagnostics;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

internal sealed class RemoteMarketTabPanel
{
    private readonly Configuration configuration;
    private readonly RemoteMarketController controller;
    private readonly RemoteSummoningBellProbe bellProbe;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public RemoteMarketTabPanel(
        Configuration configuration,
        RemoteMarketController controller,
        RemoteSummoningBellProbe bellProbe,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.configuration = configuration;
        this.controller = controller;
        this.bellProbe = bellProbe;
        this.reviewRegistry = reviewRegistry;
    }

    public void Draw()
    {
        if (!MarketAcquisitionUnlock.IsUnlocked(configuration))
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Remote market requires the private market acquisition unlock.");
            return;
        }

        var enabled = configuration.EnableRemoteMarketPurchase;
        if (ImGui.Checkbox("Enable remote market purchase", ref enabled))
        {
            configuration.EnableRemoteMarketPurchase = enabled;
            configuration.Save();
        }

        var view = controller.GetView();
        ImGui.Separator();

        if (ImGui.Button("Open market board here"))
            SubmitOpenAgent();
        ImGui.SameLine();
        if (ImGui.Button("Open via distant board object"))
            SubmitOpenViaObject();
        reviewRegistry.RegisterLastButton(
            "remote-market.open-agent",
            "Open market board here",
            true,
            SubmitOpenAgent,
            "Opens the market board agent directly.");
        reviewRegistry.RegisterLastButton(
            "remote-market.open-via-object",
            "Open via distant board object",
            true,
            SubmitOpenViaObject,
            "Stock interaction with hitbox radius and elevation augmentation.");
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{view.Listings.Count} listings staged");

        if (view.Batch is { } batch)
        {
            ImGui.Separator();
            DrawRow("Active batch", $"{batch.CompletedCount}/{batch.TotalCount} done, {batch.FailedCount} failed");
        }
        if (configuration.RemoteMarketRejectedTerritories.Count > 0)
        {
            ImGui.Separator();
            DrawRow("Rejected areas", string.Join(", ", configuration.RemoteMarketRejectedTerritories));
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                controller.ClearRejectedTerritories();
        }
        if (view.LastOutcome is not null)
        {
            ImGui.Separator();
            DrawRow("Last outcome", view.LastOutcome);
        }

        DrawBellProbe();
    }

    private void DrawBellProbe()
    {
        var view = bellProbe.GetView();
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Phase A — same-territory bell probe");
        ImGui.TextWrapped(
            "Secondary client only. Extends the loaded bell's hitbox and shadows its live/default positions to the player through the bounded response observation, then restores every field. The stock StartTalkEvent passes through unchanged.");

        var enabled = view.CanSubmit && !view.Active;
        if (!enabled)
            ImGui.BeginDisabled();
        if (ImGui.Button(view.Active ? "Observing stock interaction..." : "Run one stock bell interaction"))
            SubmitBellProbe();
        if (!enabled)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "remote-bell.submit-once",
            "Run one stock bell interaction",
            enabled,
            SubmitBellProbe,
            view.Readiness);

        if (!configuration.EnableMarketDiagnostics)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Enable Market Diagnostics in Settings to arm this probe.");

        DrawRow("Readiness", view.Readiness);
        DrawRow("State", view.State);
        DrawRow("Result", view.Message);
        if (view.BellGameObjectId is not null)
            DrawRow("Loaded bell", $"{view.BellGameObjectId} at {view.Distance:0.0}y; ordinary limit {view.OrdinaryInteractionDistance:0.0}y");
        if (view.LastEvidencePath is not null)
            DrawRow("Evidence", view.LastEvidencePath);
    }

    private void SubmitOpenAgent()
    {
        var message = controller.OpenMarketBoard();
        Plugin.ChatGui.Print($"[MMF] Remote market: {message}");
    }

    private void SubmitOpenViaObject()
    {
        var message = controller.OpenMarketBoardViaObject();
        Plugin.ChatGui.Print($"[MMF] Remote market: {message}");
    }

    private void SubmitBellProbe()
    {
        var message = bellProbe.BeginProbe();
        Plugin.ChatGui.Print($"[MMF] Remote bell probe: {message}");
    }

    private static void DrawRow(string label, string value)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{label}:");
        ImGui.SameLine(150f);
        ImGui.TextWrapped(value);
    }
}
