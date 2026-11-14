using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Settings;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketDiagnostics;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class MarketAcquisitionSettingsPages
{
    private readonly Configuration config;
    private readonly IRetainerListingRefreshControls refreshControls;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private string? lastRelogAccepted;

    public MarketAcquisitionSettingsPages(
        Configuration config,
        IRetainerListingRefreshControls refreshControls,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.refreshControls = refreshControls ?? throw new ArgumentNullException(nameof(refreshControls));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        Descriptors =
        [
            new("market.operation", "Market Acquisition / Operation", DrawOperation, 30, IsUnlocked,
                ["opportunistic world checks", "recent world TTL", "full resweep", "listing purchases", "Ul'dah", "aethernet", "world travel"]),
            new("market.diagnostics", "Market Acquisition / Diagnostics", DrawDiagnostics, 31, IsUnlocked,
                ["route diagnostic packages", "route log", "observed listings", "purchase records", "archive", "retention", "hot shelf", "keep raw", "exhaustive research", "early leaving"]),
            new("retainer-listings.refresh", "Retainer Listings / Price Refresh", DrawRetainerListingRefresh, 32, IsUnlocked,
                ["retainer listing refresh", "Universalis", "remote market board", "background", "retainer menu", "force retry", "lockout", "relog"]),
        ];
    }

    public IReadOnlyList<SettingsPageDescriptor> Descriptors { get; }

    private void DrawRetainerListingRefresh(SettingsPageContext context)
    {
        DrawCheckbox(
            context,
            "Refresh listed items when observed",
            "After changed retainer listings are observed and the retainer menu closes, refresh their market-board data in the background for Universalis uploaders. This does not purchase items or change your listing prices. Deferred or blocked work remains visible in Status.",
            () => config.EnableRetainerListingRefresh,
            value => config.EnableRetainerListingRefresh = value);
        if (config.EnableRetainerListingRefresh &&
            context.Matches("retainer listing refresh", "Universalis", "background", "deferred", "blocked", "Status", "lockout", "relog"))
        {
            var refresh = config.RetainerListingRefresh;
            var lockout = refreshControls.Lockout;
            if (lockout.Active)
            {
                DrawMarketLockout(lockout);
            }
            else
            {
                lastRelogAccepted = null;
                ImGui.TextColored(
                    refresh.NeedsAttention ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted,
                    refresh.StatusMessage);
            }
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
    }

    private void DrawMarketLockout(MarketBoardLockoutStatus lockout)
    {
        ImGui.TextColored(
            MarketMafiosoUiTheme.Warning,
            $"Market board locked out. {refreshControls.WaitingCount} refresh item(s) waiting for relog.");
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, refreshControls.DescribeLockout(lockout));
        if (ImGui.SmallButton("Relog##market-lockout"))
        {
            var result = refreshControls.RequestRelog("settings");
            lastRelogAccepted = result.Accepted ? result.Message : null;
        }
        reviewRegistry.Register(
            "settings.retainer-listings.refresh.relog",
            "Relog to clear the market board lockout",
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled: true,
            selected: false,
            value: refreshControls.RelogRefusal,
            arguments: null,
            surfaceId: "settings.retainer-listings.refresh",
            mutating: true,
            completionOperationKind: null,
            _ =>
            {
                // Same guarded path as the button: MarketRelogPolicy refuses while the character or MMF is busy.
                var result = refreshControls.RequestRelog("agent-bridge");
                lastRelogAccepted = result.Accepted ? result.Message : null;
                return result.Accepted
                    ? AgentBridgeUiActionResult.Ok(result.Message)
                    : AgentBridgeUiActionResult.Fail(result.Message);
            });
        // The refusal is re-evaluated every frame by the controls, so it disappears once its cause ends.
        var status = refreshControls.RelogRefusal is { } refusal ? $"Refused: {refusal}" : lastRelogAccepted;
        if (!string.IsNullOrWhiteSpace(status))
        {
            ImGui.SameLine();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, status);
        }
    }

    private void DrawOperation(SettingsPageContext context)
    {
        DrawCheckbox(context, "Leave Ul'dah market via aethernet before world travel",
            "Walk to Sapphire Avenue Exchange and transfer to Aetheryte Plaza before asking Lifestream to visit the next world. If unavailable, use normal Lifestream travel. Lifestream still handles world travel and the destination market board.",
            () => config.MarketAcquisitionAethernetDeparture, value => config.MarketAcquisitionAethernetDeparture = value);
        DrawCheckbox(context, "Enable listing purchases",
            "Allows acquisition commands and integrations to open the market board and purchase confirmed listings.",
            () => config.EnableMarketListingPurchases, value => config.EnableMarketListingPurchases = value);
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
        if (context.Matches("Minimum stop value", "gil", "Recommended", "route"))
        {
            var value = config.MarketAcquisitionMinimumStopValueGil;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputInt("Minimum stop value (gil)", ref value))
            {
                config.MarketAcquisitionMinimumStopValueGil = Math.Max(0, value);
                config.Save();
            }
            ImGui.TextWrapped("Recommended routes skip stops below this estimated value. 0 disables the minimum. All-world sweeps and exhaustive research never drop stops by value. Applies when preparing a plan.");
        }
    }

    private void DrawDiagnostics(SettingsPageContext context)
    {
        const string label = "Route diagnostics";
        const string description = "Summary records route decisions, purchases, timing, and failures. Full trace adds segmented replay evidence.";
        if (!context.Matches(label, description, "Off", "Summary", "Full trace", "archive", "retention", "hot shelf", "keep raw", "exhaustive research", "early leaving"))
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

        var exhaustiveResearch = config.MarketAcquisitionExhaustiveResearchMode;
        if (ImGui.Checkbox("Exhaustive research mode", ref exhaustiveResearch))
        {
            config.MarketAcquisitionExhaustiveResearchMode = exhaustiveResearch;
            config.Save();
        }
        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            "Wait for the full browse and visible-listing continuation path before leaving each search. Routes take longer; turn this off for decision-ready acquisition speed.");
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
            refreshControls.ForceRetry(item.ItemId);
        reviewRegistry.Register(
            $"settings.retainer-listings.refresh.{item.ItemId}.force-retry",
            $"Force retry {FormatItem(item)}",
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled: true,
            selected: false,
            value: item.LastCode,
            arguments: null,
            surfaceId: "settings.retainer-listings.refresh",
            mutating: true,
            completionOperationKind: null,
            _ => refreshControls.ForceRetry(item.ItemId)
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
