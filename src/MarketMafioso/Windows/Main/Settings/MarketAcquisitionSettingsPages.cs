using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Settings;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class MarketAcquisitionSettingsPages
{
    private readonly Configuration config;

    public MarketAcquisitionSettingsPages(Configuration config)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        Descriptors =
        [
            new("market.operation", "Market Acquisition / Operation", DrawOperation, 30, IsUnlocked,
                ["opportunistic world checks", "recent world TTL", "full resweep", "listing purchases", "retainer listing refresh", "Universalis"]),
            new("market.diagnostics", "Market Acquisition / Diagnostics", DrawDiagnostics, 31, IsUnlocked,
                ["route diagnostic packages", "route log", "observed listings", "purchase records"]),
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
                var blocked = refresh.Items.Count(item => item.State == RetainerListingRefreshItemState.Blocked);
                ImGui.TextColored(
                    blocked > 0 ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted,
                    $"{refresh.Items.Count} item(s) retained for refresh; {blocked} blocked.");
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
        if (!context.Matches(label, description, "Off", "Summary", "Full trace"))
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
    }

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);

    private static string FormatDiagnosticsLevel(MarketAcquisitionRouteDiagnosticsLevel level) => level switch
    {
        MarketAcquisitionRouteDiagnosticsLevel.Off => "Off",
        MarketAcquisitionRouteDiagnosticsLevel.FullTrace => "Full trace",
        _ => "Summary",
    };

    private bool IsUnlocked() => MarketAcquisitionUnlock.IsUnlocked(config);
}
