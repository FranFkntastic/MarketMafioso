using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.SquireIntegration;

internal sealed class StandaloneSquirePanel
{
    private readonly StandaloneSquireIpcClient squire;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public StandaloneSquirePanel(StandaloneSquireIpcClient squire, AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.squire = squire;
        this.reviewRegistry = reviewRegistry;
    }

    public void Draw()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Squire");
        ImGui.TextWrapped("Equipment planning, Outfitter, and reviewed cleanup now run in the standalone Squire plugin.");
        if (squire.TryGetSnapshot(out var snapshot, out var error))
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Success, "Standalone Squire is connected.");
            if (ImGui.Button("Open Squire"))
                squire.TryOpen(out _);
            reviewRegistry.Register(
                "squire.launcher.open",
                "Open standalone Squire",
                AgentBridgeUiControlKind.Button,
                ImGui.GetItemRectMin(),
                ImGui.GetItemRectMax(),
                true,
                false,
                null,
                () => squire.TryOpen(out _));
            if (snapshot is not null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Workspace: {snapshot.Workspace}");
            return;
        }

        ImGui.TextColored(MarketMafiosoUiTheme.Warning, "Standalone Squire is not available.");
        ImGui.TextWrapped(error ?? "Install or enable Squire, then reopen this tab.");
    }
}
