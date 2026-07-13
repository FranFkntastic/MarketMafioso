using System;
using System.Collections.Generic;
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
                ["opportunistic world checks", "recent world TTL", "full resweep"]),
            new("market.diagnostics", "Market Acquisition / Diagnostics", DrawDiagnostics, 31, IsUnlocked,
                ["route diagnostic packages", "route log", "observed listings", "purchase records"]),
        ];
    }

    public IReadOnlyList<SettingsPageDescriptor> Descriptors { get; }

    private void DrawOperation(SettingsPageContext context)
    {
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

    private void DrawDiagnostics(SettingsPageContext context) => DrawCheckbox(context, "Create route diagnostic packages",
        "When enabled, every guided route writes route.log plus observed-listings and purchase-record CSVs.",
        () => config.CreateMarketAcquisitionRouteDiagnosticPackages, value => config.CreateMarketAcquisitionRouteDiagnosticPackages = value);

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);

    private bool IsUnlocked() => MarketAcquisitionUnlock.IsUnlocked(config);
}
