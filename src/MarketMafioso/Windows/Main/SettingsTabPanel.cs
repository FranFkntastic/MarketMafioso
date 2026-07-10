using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.Main;

internal sealed class SettingsTabPanel
{
    private const string LocalReceiverUrl = "http://localhost:8080/inventory";
    private const string DevReceiverUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory";

    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly IPluginLog log;
    private readonly Action stopMarketAcquisitionRoute;
    private readonly Action closeAcquisitionDiagnostics;
    private readonly Action closeAutomationDiagnostics;
    private readonly Action openAutomationDiagnostics;

    private string urlBuffer;
    private string apiKeyBuffer;
    private string dashboardUrlBuffer = string.Empty;
    private string dashboardOpenStatus = "Dashboard link appears after a successful send.";
    private string marketAcquisitionUnlockKeyBuffer = string.Empty;
    private string marketAcquisitionUnlockStatus = "Private module is hidden until unlocked.";
    private bool showApiKey;
    private bool showMarketAcquisitionUnlockKey;

    public SettingsTabPanel(
        Configuration config,
        HttpReporter reporter,
        IPluginLog log,
        Action stopMarketAcquisitionRoute,
        Action closeAcquisitionDiagnostics,
        Action closeAutomationDiagnostics,
        Action openAutomationDiagnostics)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.stopMarketAcquisitionRoute = stopMarketAcquisitionRoute ?? throw new ArgumentNullException(nameof(stopMarketAcquisitionRoute));
        this.closeAcquisitionDiagnostics = closeAcquisitionDiagnostics ?? throw new ArgumentNullException(nameof(closeAcquisitionDiagnostics));
        this.closeAutomationDiagnostics = closeAutomationDiagnostics ?? throw new ArgumentNullException(nameof(closeAutomationDiagnostics));
        this.openAutomationDiagnostics = openAutomationDiagnostics ?? throw new ArgumentNullException(nameof(openAutomationDiagnostics));
        urlBuffer = config.ServerUrl;
        apiKeyBuffer = config.ApiKey;
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Plugin Settings");
        ImGui.TextWrapped("Shared MarketMafioso client/server settings used by Inventory Reporter, Workshop Logistics, and receiver-backed features.");
        ImGui.Spacing();

        DrawServerSection();
        ImGui.Spacing();
        DrawInternalFeatureSettingsSection();
        if (IsMarketAcquisitionUnlocked())
        {
            ImGui.Spacing();
            DrawMarketAcquisitionSettingsSection();
        }
    }

    private void DrawServerSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Server Connection");
        ImGui.Separator();

        ImGui.Text("Server URL:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##url", ref urlBuffer, 512))
        {
            config.ServerUrl = urlBuffer;
            config.Save();
        }

        if (ImGui.Button("Local Receiver"))
            ApplyServerUrlPreset(LocalReceiverUrl);
        ImGui.SameLine();
        if (ImGui.Button("Dev VPS"))
            ApplyServerUrlPreset(DevReceiverUrl);
        ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.Button("Production VPS (future)");
        ImGui.EndDisabled();

        var endpoint = ReceiverEndpointClassifier.Classify(urlBuffer);
        var requiresApiKey = endpoint.RequiresApiKey;
        ImGui.Text(requiresApiKey
            ? "Client API Key (required for this endpoint):"
            : "Client API Key (optional - sent as X-Api-Key header):");
        var keyWidth = ImGui.GetContentRegionAvail().X - 70;
        ImGui.SetNextItemWidth(keyWidth);
        var flags = showApiKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputText("##apikey", ref apiKeyBuffer, 256, flags))
        {
            config.ApiKey = apiKeyBuffer;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button(showApiKey ? "Hide##k" : "Show##k", new Vector2(60, 0)))
            showApiKey = !showApiKey;

        if (endpoint.Kind == ReceiverEndpointKind.Invalid)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Enter a valid HTTP or HTTPS receiver URL.");
        else if (requiresApiKey && string.IsNullOrWhiteSpace(apiKeyBuffer))
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "This endpoint requires a client API key before plugin requests can be sent.");
        else if (endpoint.Kind == ReceiverEndpointKind.CustomRemote)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Custom remote endpoint. Client API key is required by default.");

        ImGui.Spacing();
        DrawDashboardOpenSection();
    }

    private void DrawMarketAcquisitionSettingsSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Market Acquisition");
        ImGui.Separator();

        var enableOpportunistic = config.EnableOpportunisticWorldChecks;
        if (ImGui.Checkbox("Check every batch item on each visited world", ref enableOpportunistic))
        {
            config.EnableOpportunisticWorldChecks = enableOpportunistic;
            config.Save();
        }

        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            "Default on. While already on a world, MarketMafioso checks other unfinished items from the same claimed batch.");

        ImGui.Spacing();
        var createRouteDiagnostics = config.CreateMarketAcquisitionRouteDiagnosticPackages;
        if (ImGui.Checkbox("Create route diagnostic packages", ref createRouteDiagnostics))
        {
            config.CreateMarketAcquisitionRouteDiagnosticPackages = createRouteDiagnostics;
            config.Save();
        }

        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            "When enabled, every guided route writes route.log plus observed-listings and purchase-record CSVs.");

        ImGui.Spacing();
        var recentWorldTtlHours = config.MarketAcquisitionRecentWorldTtlHours;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("All-world recent check TTL (hours)", ref recentWorldTtlHours))
        {
            config.MarketAcquisitionRecentWorldTtlHours = Math.Clamp(recentWorldTtlHours, 1, 168);
            config.Save();
        }

        var ignoreRecentVisits = config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep;
        if (ImGui.Checkbox("Full all-world resweep", ref ignoreRecentVisits))
        {
            config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep = ignoreRecentVisits;
            config.Save();
        }

        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            "Default TTL is 18h. Full resweep ignores recent checked worlds while preparing all-world routes.");
    }

    private void DrawInternalFeatureSettingsSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Internal Features");
        ImGui.Separator();

        DrawCraftQuoteSettingsSection();
        ImGui.Spacing();
        DrawAgentBridgeSettingsSection();
        ImGui.Spacing();

        if (IsMarketAcquisitionUnlocked())
        {
            var unlockedAt = config.MarketAcquisitionUnlockedAtUtc == null
                ? "enabled"
                : $"enabled {config.MarketAcquisitionUnlockedAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC";
            ImGui.TextColored(MarketMafiosoUiTheme.Success, $"Market Acquisition {unlockedAt}.");
            ImGui.SameLine();
            if (ImGui.Button("Lock Market Acquisition"))
            {
                stopMarketAcquisitionRoute();
                closeAcquisitionDiagnostics();
                closeAutomationDiagnostics();
                MarketAcquisitionUnlock.Lock(config);
                config.Save();
                marketAcquisitionUnlockKeyBuffer = string.Empty;
                marketAcquisitionUnlockStatus = "Private module locked.";
            }

            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Locking hides the UI only. Existing local request state and server data are left untouched.");
            if (ImGui.Button("Automation Diagnostics"))
                openAutomationDiagnostics();
            return;
        }

        closeAutomationDiagnostics();

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Private/internal modules are hidden by default.");
        ImGui.Text("Unlock key:");
        var keyWidth = ImGui.GetContentRegionAvail().X - 82;
        ImGui.SetNextItemWidth(Math.Max(120f, keyWidth));
        var flags = showMarketAcquisitionUnlockKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.InputText("##marketAcquisitionUnlockKey", ref marketAcquisitionUnlockKeyBuffer, 256, flags);
        ImGui.SameLine();
        if (ImGui.Button(showMarketAcquisitionUnlockKey ? "Hide##marketAcquisitionUnlock" : "Show##marketAcquisitionUnlock", new Vector2(72, 0)))
            showMarketAcquisitionUnlockKey = !showMarketAcquisitionUnlockKey;

        if (ImGuiUi.Button("Unlock private module", !string.IsNullOrWhiteSpace(marketAcquisitionUnlockKeyBuffer)))
        {
            if (MarketAcquisitionUnlock.TryUnlock(config, marketAcquisitionUnlockKeyBuffer))
            {
                config.Save();
                marketAcquisitionUnlockKeyBuffer = string.Empty;
                marketAcquisitionUnlockStatus = "Private module unlocked.";
            }
            else
            {
                marketAcquisitionUnlockStatus = "Unlock key was not accepted.";
            }
        }

        ImGui.TextColored(
            marketAcquisitionUnlockStatus.Contains("not accepted", StringComparison.OrdinalIgnoreCase)
                ? MarketMafiosoUiTheme.Error
                : MarketMafiosoUiTheme.Muted,
            marketAcquisitionUnlockStatus);
    }

    private void DrawCraftQuoteSettingsSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Craft Quote Evidence");

        var enableWorkshopHostQuotes = config.EnableWorkshopHostCraftQuotes;
        if (ImGui.Checkbox("Enable Workshop Host craft quotes", ref enableWorkshopHostQuotes))
        {
            config.EnableWorkshopHostCraftQuotes = enableWorkshopHostQuotes;
            config.Save();
        }

        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            "Uses the configured Workshop Host service for advisory craft-cost evidence when the host advertises craft.appraise.");

        var enableManualFallback = config.EnableCraftArchitectManualFallback;
        if (ImGui.Checkbox("Enable manual craft-cost fallback", ref enableManualFallback))
        {
            config.EnableCraftArchitectManualFallback = enableManualFallback;
            config.Save();
        }

        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            "Default off. Workshop Host should be the normal quote path; manual craft cost entry is only for local troubleshooting.");
    }

    private void DrawAgentBridgeSettingsSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Agent Test Bridge");
        var enabled = config.EnableAgentBridge;
        if (ImGui.Checkbox("Enable local agent test bridge", ref enabled))
        {
            config.EnableAgentBridge = enabled;
            config.Save();
        }

        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            "Dev-only named-pipe bridge. It exposes read-only state plus open-window/proof commands; it cannot start routes or make purchases.");
    }

    private void DrawDashboardOpenSection()
    {
        var dashboardUrl = HttpReporter.ResolveDashboardUrlForDisplay(reporter.LastDashboardUrl, urlBuffer) ?? string.Empty;
        if (!string.Equals(dashboardUrlBuffer, dashboardUrl, StringComparison.Ordinal))
            dashboardUrlBuffer = dashboardUrl;

        ImGui.Text("Dashboard URL:");
        var buttonWidth = 128f;
        var inputWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputText("##dashboardUrl", ref dashboardUrlBuffer, 1024, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiUi.Button("Open Dashboard", new Vector2(buttonWidth, 0), !string.IsNullOrWhiteSpace(dashboardUrl)))
            OpenDashboardUrl(dashboardUrl);

        var status = string.IsNullOrWhiteSpace(dashboardUrl)
            ? dashboardOpenStatus
            : string.IsNullOrWhiteSpace(reporter.LastDashboardUrl)
                ? "Dashboard link derived from endpoint."
                : dashboardOpenStatus;
        ImGui.TextColored(GetDashboardOpenStatusColor(status), status);
    }

    private void OpenDashboardUrl(string dashboardUrl)
    {
        if (!Uri.TryCreate(dashboardUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            dashboardOpenStatus = "Dashboard URL is not a valid HTTP or HTTPS link.";
            log.Warning($"[MarketMafioso] Refusing to open invalid dashboard URL: {dashboardUrl}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString())
            {
                UseShellExecute = true,
            });
            dashboardOpenStatus = "Opened dashboard in external browser.";
        }
        catch (Exception ex)
        {
            dashboardOpenStatus = $"Unable to open dashboard. {ex.Message}";
            log.Error(ex, "[MarketMafioso] Unable to open dashboard URL.");
        }
    }

    private static Vector4 GetDashboardOpenStatusColor(string status) =>
        status.StartsWith("Unable", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("not a valid", StringComparison.OrdinalIgnoreCase)
            ? MarketMafiosoUiTheme.Error
            : MarketMafiosoUiTheme.Muted;

    private bool IsMarketAcquisitionUnlocked() => MarketAcquisitionUnlock.IsUnlocked(config);

    private void ApplyServerUrlPreset(string serverUrl)
    {
        urlBuffer = serverUrl;
        config.ServerUrl = serverUrl;
        config.Save();
    }
}
