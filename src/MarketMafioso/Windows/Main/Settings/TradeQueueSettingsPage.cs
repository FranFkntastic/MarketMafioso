using System;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Settings;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class TradeQueueSettingsPage
{
    private static readonly AgentBridgeActionArgumentSchema AutoAcceptSchema = new(
    [
        new("enabled", AgentBridgeActionArgumentKind.Boolean),
    ]);

    private readonly Configuration config;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public TradeQueueSettingsPage(Configuration config, AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
    }

    public SettingsPageDescriptor Descriptor => new(
        "trade-queue.incoming",
        "Trade Queue / Incoming Trades",
        Draw,
        20,
        searchTerms: ["auto-accept", "ready", "confirm", "incoming trade"]);

    private void Draw(SettingsPageContext context)
    {
        const string label = "Handle incoming trades automatically";
        const string description = "Become ready after the other player is ready, then confirm only when the completed trade is available. Manual controls remain available when automation pauses or fails.";
        if (!context.Matches(label, description, "auto-accept", "ready", "confirm", "incoming trade"))
            return;

        var enabled = config.AutoAcceptIncomingTrades;
        if (ImGui.Checkbox(label, ref enabled))
        {
            config.AutoAcceptIncomingTrades = enabled;
            config.Save();
        }
        reviewRegistry.Register(
            "settings.trade-queue.incoming.auto-accept",
            label,
            AgentBridgeUiControlKind.Toggle,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            true,
            config.AutoAcceptIncomingTrades,
            config.AutoAcceptIncomingTrades ? "enabled" : "disabled",
            AutoAcceptSchema,
            "settings.trade-queue.incoming",
            true,
            null,
            arguments =>
            {
                config.AutoAcceptIncomingTrades = arguments!.Value.GetProperty("enabled").GetBoolean();
                config.Save();
                return AgentBridgeUiActionResult.Ok(
                    config.AutoAcceptIncomingTrades
                        ? "Incoming trade automation enabled."
                        : "Incoming trade automation disabled.");
            });
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, description);
    }
}
