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
        reviewRegistry.RegisterLastButton(
            "remote-market.open-agent",
            "Open market board here",
            true,
            SubmitOpenAgent,
            "Opens the market board agent directly.");
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

        DrawNormalBellCapture();
        DrawYieldEventSceneProbe();
        DrawBellProbe();
    }

    private void DrawYieldEventSceneProbe()
    {
        var view = bellProbe.GetYieldProbeView();
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Bell-less YieldEventScene2 probe");
        ImGui.TextWrapped(
            "Two-step packet test. The control replaces one normal retainer-selection packet with an exact clone inside a valid bell session. The direct test replays that confirmed current-build packet once after every retainer window is closed.");

        var controlEnabled = view.CanArmControl && !view.Active;
        if (!controlEnabled)
            ImGui.BeginDisabled();
        if (ImGui.Button(view.Active ? "Yield probe active..." : "Arm in-session control"))
            SubmitYieldControl();
        if (!controlEnabled)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "remote-bell.yield-control",
            "Arm in-session YieldEventScene2 control",
            controlEnabled,
            SubmitYieldControl,
            view.Readiness);

        ImGui.SameLine();
        var directEnabled = view.CanReplaySessionFree && !view.Active;
        if (!directEnabled)
            ImGui.BeginDisabled();
        if (ImGui.Button("Send one session-free replay"))
            SubmitYieldDirect();
        if (!directEnabled)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "remote-bell.yield-direct",
            "Send one session-free YieldEventScene2 replay",
            directEnabled,
            SubmitYieldDirect,
            view.Readiness);

        DrawRow("Readiness", view.Readiness);
        DrawRow("Mode", view.Mode);
        DrawRow("State", view.State);
        DrawRow("Result", view.Message);
        if (view.RetainerId is not null)
            DrawRow("Retainer", view.RetainerId);
        if (view.Opcode is not null)
            DrawRow("Opcode", view.Opcode);
        if (view.LastEvidencePath is not null)
            DrawRow("Evidence", view.LastEvidencePath);
    }

    private void DrawNormalBellCapture()
    {
        var view = bellProbe.GetNormalCaptureView();
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Normal bell flight recorder");
        ImGui.TextWrapped(
            "Passive baseline capture. Records the stock StartTalkEvent, bounded inbound/outbound zone traffic, event callbacks, and client session-state transitions through one normal retainer selection. It does not alter packets or game state.");

        var enabled = view.CanArm && !view.Active;
        if (!enabled)
            ImGui.BeginDisabled();
        if (ImGui.Button(view.Active ? "Normal bell recorder armed..." : "Arm normal bell recorder"))
            SubmitNormalBellCapture();
        if (!enabled)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "remote-bell.capture-normal",
            "Arm normal bell recorder",
            enabled,
            SubmitNormalBellCapture,
            view.Readiness);

        DrawRow("Readiness", view.Readiness);
        DrawRow("State", view.State);
        DrawRow("Result", view.Message);
        if (view.BellGameObjectId is not null)
            DrawRow("Loaded bell", $"{view.BellGameObjectId} at {view.Distance:0.0}y; ordinary limit {view.OrdinaryInteractionDistance:0.0}y");
        if (view.LastEvidencePath is not null)
            DrawRow("Evidence", view.LastEvidencePath);
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

    private void SubmitBellProbe()
    {
        var message = bellProbe.BeginProbe();
        Plugin.ChatGui.Print($"[MMF] Remote bell probe: {message}");
    }

    private void SubmitNormalBellCapture()
    {
        var message = bellProbe.BeginNormalCapture();
        Plugin.ChatGui.Print($"[MMF] Normal bell capture: {message}");
    }

    private void SubmitYieldControl()
    {
        var message = bellProbe.BeginYieldControl();
        Plugin.ChatGui.Print($"[MMF] YieldEventScene2 control: {message}");
    }

    private void SubmitYieldDirect()
    {
        var message = bellProbe.BeginYieldSessionFreeReplay();
        Plugin.ChatGui.Print($"[MMF] YieldEventScene2 direct probe: {message}");
    }

    private static void DrawRow(string label, string value)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{label}:");
        ImGui.SameLine(150f);
        ImGui.TextWrapped(value);
    }
}
