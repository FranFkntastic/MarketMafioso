using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketMafioso.AgentBridge;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

public sealed class AgentBridgeProofWindow : Window
{
    private readonly AgentBridgeProofStore proofStore;

    public AgentBridgeProofWindow(AgentBridgeProofStore proofStore)
        : base("MMF Agent Bridge Proof##MarketMafiosoAgentBridgeProof")
    {
        this.proofStore = proofStore ?? throw new ArgumentNullException(nameof(proofStore));
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public string? RequestedProofId { get; set; }

    public override void Draw()
    {
        var receipt = string.IsNullOrWhiteSpace(RequestedProofId)
            ? proofStore.GetCurrent()
            : proofStore.Get(RequestedProofId);
        if (receipt == null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No bridge proof has been captured yet.");
            return;
        }

        proofStore.MarkPresented(receipt.ProofId);
        receipt = proofStore.Get(receipt.ProofId)!;
        var truth = receipt.Truth;
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Agent Bridge Proof");
        ImGui.TextWrapped("This panel and the local bridge response are rendered from the same frozen truth receipt.");
        ImGui.Separator();
        DrawRow("Challenge", string.IsNullOrWhiteSpace(receipt.Challenge) ? "(none)" : receipt.Challenge);
        DrawRow("Proof ID", receipt.ProofId);
        DrawRow("Truth SHA-256", receipt.TruthSha256);
        DrawRow("Proof SHA-256", receipt.ProofSha256);
        DrawRow("Revision", receipt.Revision.ToString());
        DrawRow("Captured (UTC)", receipt.CapturedAtUtc.ToString("O"));
        DrawRow("Presented", receipt.PresentedInGame ? "Yes" : "Rendering now");
        ImGui.Separator();
        DrawRow("Instance", $"PID {truth.ProcessId} | {truth.PluginVersion}");
        DrawRow("Character / World", $"{truth.CharacterName}@{truth.CurrentWorld} (home {truth.HomeWorld})");
        DrawRow("Workspace", truth.WorkspaceStatus);
        DrawRow("Route", $"{truth.Route.State}: {truth.Route.StatusMessage}");
        DrawRow("Active stop", $"{truth.Route.ActiveWorld ?? "None"} / {truth.Route.ActiveStopStatus ?? "None"}");
        DrawRow("Active operation", $"{truth.Route.ActiveOperationKind ?? "None"} / {truth.Route.ActiveOperationPhase ?? "None"} / {truth.Route.ActiveOperationDisposition ?? "None"}");
    }

    private static void DrawRow(string label, string value)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{label}:");
        ImGui.SameLine(180f);
        ImGui.TextWrapped(value);
    }
}
