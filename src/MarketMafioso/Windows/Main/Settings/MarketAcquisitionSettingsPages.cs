using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Settings;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class MarketAcquisitionSettingsPages
{
    private readonly Configuration config;
    private readonly Func<uint, bool> forceRetryRetainerListingRefresh;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public MarketAcquisitionSettingsPages(
        Configuration config,
        Func<uint, bool> forceRetryRetainerListingRefresh,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.forceRetryRetainerListingRefresh = forceRetryRetainerListingRefresh
            ?? throw new ArgumentNullException(nameof(forceRetryRetainerListingRefresh));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        Descriptors =
        [
            new("market.operation", "Market Acquisition / Operation", DrawOperation, 30, IsUnlocked,
                ["opportunistic world checks", "recent world TTL", "full resweep", "listing purchases", "retainer listing refresh", "Universalis"]),
            new("market.diagnostics", "Market Acquisition / Diagnostics", DrawDiagnostics, 31, IsUnlocked,
                ["route diagnostic packages", "route log", "observed listings", "purchase records", "archive", "retention", "hot shelf", "keep raw"]),
        ];
    }

    public IReadOnlyList<SettingsPageDescriptor> Descriptors { get; }

    private void DrawOperation(SettingsPageContext context)
    {
        DrawCheckbox(context, "Enable listing purchases",
            "Allows acquisition commands and integrations to open the market board and purchase confirmed listings.",
            () => config.EnableMarketListingPurchases, value => config.EnableMarketListingPurchases = value);
        DrawCheckbox(
            context,
            "Refresh listed items when observed",
            "When retainer listings are captured, quietly refresh every distinct item currently listed by this character. Healthy refreshes stay in the background; deferred or blocked work remains visible in Status.",
            () => config.EnableRetainerListingRefresh,
            value => config.EnableRetainerListingRefresh = value);
        if (config.EnableRetainerListingRefresh &&
            context.Matches("retainer listing refresh", "Universalis", "background", "deferred", "blocked", "Status"))
        {
            var refresh = config.RetainerListingRefresh;
            ImGui.TextColored(
                refresh.NeedsAttention ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted,
                refresh.StatusMessage);
            if (refresh.Items.Count > 0)
            {
                var blockedItems = refresh.Items
                    .Where(item => item.State == RetainerListingRefreshItemState.Blocked)
                    .OrderBy(item => item.ItemName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ItemId)
                    .ToArray();
                ImGui.TextColored(
                    blockedItems.Length > 0 ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted,
                    $"{refresh.Items.Count} item(s) retained for refresh; {blockedItems.Length} blocked.");
                foreach (var item in blockedItems)
                    DrawBlockedRefreshItem(item);
            }
            ImGui.Spacing();
        }
        DrawCheckbox(context, "Check every batch item on each visited world",
            "Default on. While already on a world, MarketMafioso checks other unfinished items from the same claimed batch.",
            () => config.EnableOpportunisticWorldChecks, value => config.EnableOpportunisticWorldChecks = value);
        if (context.Matches("All-world recent check TTL", "hours", "recent visit", "world cache"))
        {
            var value = config.MarketAcquisitionRecentWorldTtlHours;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputInt("All-world recent check TTL (hours)", ref value))
            {
                config.MarketAcquisitionRecentWorldTtlHours = Math.Clamp(value, 1, 168);
                config.Save();
            }
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Worlds checked within this interval can be skipped while preparing an all-world route.");
        }
        DrawCheckbox(context, "Full all-world resweep", "Ignore recent checked-world history while preparing an all-world route.",
            () => config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep, value => config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep = value);
    }

    private void DrawDiagnostics(SettingsPageContext context)
    {
        const string label = "Route diagnostics";
        const string description = "Summary records route decisions, purchases, timing, and failures. Full trace adds segmented replay evidence.";
        if (!context.Matches(label, description, "Off", "Summary", "Full trace", "archive", "retention", "hot shelf", "keep raw"))
            return;

        var current = config.MarketAcquisitionRouteDiagnostics;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo(label, FormatDiagnosticsLevel(current)))
        {
            foreach (var level in Enum.GetValues<MarketAcquisitionRouteDiagnosticsLevel>())
            {
                if (!ImGui.Selectable(FormatDiagnosticsLevel(level), level == current))
                    continue;

                config.MarketAcquisitionRouteDiagnostics = level;
                config.Save();
            }
            ImGui.EndCombo();
        }
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, description);
        ImGui.Spacing();

        var archiveCompleted = config.ArchiveCompletedMarketAcquisitionRouteDiagnostics;
        if (ImGui.Checkbox("Archive older successful diagnostics", ref archiveCompleted))
        {
            config.ArchiveCompletedMarketAcquisitionRouteDiagnostics = archiveCompleted;
            config.Save();
        }
        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            "Recent human logs and market CSVs stay directly readable. Machine streams compress when a capture closes; only older successful packages become fully compressed.");

        var hotDays = config.MarketAcquisitionRouteDiagnosticsHotDays;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Keep raw for at least this many days", ref hotDays))
        {
            config.MarketAcquisitionRouteDiagnosticsHotDays = Math.Clamp(hotDays, 1, 3_650);
            config.Save();
        }

        var hotRuns = config.MarketAcquisitionRouteDiagnosticsHotRuns;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Always keep this many successful runs raw", ref hotRuns))
        {
            config.MarketAcquisitionRouteDiagnosticsHotRuns = Math.Clamp(hotRuns, 0, 10_000);
            config.Save();
        }
        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            $"Create an empty '{MarketAcquisitionRouteDiagnosticRetention.KeepRawMarkerFileName}' file inside any package to keep it on the hot shelf.");
        ImGui.Spacing();
    }

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);

    private void DrawBlockedRefreshItem(PersistedRetainerListingRefreshItem item)
    {
        ImGui.PushID($"retainer-listing-refresh-{item.ItemId}");
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(item.ItemName) ? $"Item {item.ItemId}" : item.ItemName);
        ImGui.SameLine();
        if (ImGui.SmallButton("Force retry"))
            forceRetryRetainerListingRefresh(item.ItemId);
        reviewRegistry.Register(
            $"settings.market.operation.retainer-listing-refresh.{item.ItemId}.force-retry",
            $"Force retry {FormatItem(item)}",
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled: true,
            selected: false,
            value: item.LastCode,
            arguments: null,
            surfaceId: "settings.market.operation",
            mutating: true,
            completionOperationKind: null,
            _ => forceRetryRetainerListingRefresh(item.ItemId)
                ? AgentBridgeUiActionResult.Ok($"Queued a force retry for {FormatItem(item)}.")
                : AgentBridgeUiActionResult.Fail($"{FormatItem(item)} is no longer blocked."));
        if (!string.IsNullOrWhiteSpace(item.LastMessage))
        {
            ImGui.Indent();
            ImGui.PushStyleColor(ImGuiCol.Text, MarketMafiosoUiTheme.Muted);
            ImGui.TextWrapped(item.LastMessage);
            ImGui.PopStyleColor();
            ImGui.Unindent();
        }
        ImGui.PopID();
    }

    private static string FormatItem(PersistedRetainerListingRefreshItem item) =>
        string.IsNullOrWhiteSpace(item.ItemName) ? $"item {item.ItemId}" : item.ItemName!;

    private static string FormatDiagnosticsLevel(MarketAcquisitionRouteDiagnosticsLevel level) => level switch
    {
        MarketAcquisitionRouteDiagnosticsLevel.Off => "Off",
        MarketAcquisitionRouteDiagnosticsLevel.FullTrace => "Full trace",
        _ => "Summary",
    };

    private bool IsUnlocked() => MarketAcquisitionUnlock.IsUnlocked(config);
}
