using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.UI.Settings;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class ServerConnectionSettingsPage
{
    private const string LocalReceiverUrl = "http://localhost:8080/inventory";
    private const string DevReceiverUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory";
    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly IPluginLog log;
    private string urlBuffer;
    private string apiKeyBuffer;
    private string dashboardUrlBuffer = string.Empty;
    private string dashboardOpenStatus = "Dashboard link appears after a successful send.";
    private bool showApiKey;

    public ServerConnectionSettingsPage(Configuration config, HttpReporter reporter, IPluginLog log)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        urlBuffer = config.ServerUrl;
        apiKeyBuffer = config.ApiKey;
        Descriptor = new SettingsPageDescriptor(
            "general.server",
            "General / Server Connection",
            Draw,
            0,
            searchTerms: ["receiver URL", "API key", "dashboard", "local receiver", "development server"]);
    }

    public SettingsPageDescriptor Descriptor { get; }

    private void Draw(SettingsPageContext context)
    {
        if (context.Matches("Server URL", "receiver endpoint", "local receiver", "development server"))
        {
            ImGui.Text("Server URL:");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##url", ref urlBuffer, 512))
            {
                config.ServerUrl = urlBuffer;
                config.Save();
            }

            if (ImGui.Button("Local Receiver")) ApplyServerUrlPreset(LocalReceiverUrl);
            ImGui.SameLine();
            if (ImGui.Button("Dev VPS")) ApplyServerUrlPreset(DevReceiverUrl);
            ImGui.SameLine();
            ImGui.BeginDisabled();
            ImGui.Button("Production VPS (future)");
            ImGui.EndDisabled();
            ImGui.Spacing();
        }

        var endpoint = ReceiverEndpointClassifier.Classify(urlBuffer);
        if (context.Matches("Client API key", "X-Api-Key", "authentication", "receiver endpoint"))
        {
            ImGui.Text(endpoint.RequiresApiKey
                ? "Client API Key (required for this endpoint):"
                : "Client API Key (optional - sent as X-Api-Key header):");
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 70);
            var flags = showApiKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
            if (ImGui.InputText("##apikey", ref apiKeyBuffer, 256, flags))
            {
                config.ApiKey = apiKeyBuffer;
                config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button(showApiKey ? "Hide##k" : "Show##k", new Vector2(60, 0))) showApiKey = !showApiKey;

            if (endpoint.Kind == ReceiverEndpointKind.Invalid)
                ImGui.TextColored(MarketMafiosoUiTheme.Error, "Enter a valid HTTP or HTTPS receiver URL.");
            else if (endpoint.RequiresApiKey && string.IsNullOrWhiteSpace(apiKeyBuffer))
                ImGui.TextColored(MarketMafiosoUiTheme.Error, "This endpoint requires a client API key before plugin requests can be sent.");
            else if (endpoint.Kind == ReceiverEndpointKind.CustomRemote)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Custom remote endpoint. Client API key is required by default.");
            ImGui.Spacing();
        }

        if (context.Matches("Dashboard URL", "open dashboard", "receiver dashboard"))
            DrawDashboard();
    }

    private void DrawDashboard()
    {
        var dashboardUrl = HttpReporter.ResolveDashboardUrlForDisplay(reporter.LastDashboardUrl, urlBuffer) ?? string.Empty;
        if (!string.Equals(dashboardUrlBuffer, dashboardUrl, StringComparison.Ordinal)) dashboardUrlBuffer = dashboardUrl;
        ImGui.Text("Dashboard URL:");
        const float buttonWidth = 128f;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputText("##dashboardUrl", ref dashboardUrlBuffer, 1024, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiUi.Button("Open Dashboard", new Vector2(buttonWidth, 0), !string.IsNullOrWhiteSpace(dashboardUrl))) OpenDashboardUrl(dashboardUrl);
        var status = string.IsNullOrWhiteSpace(dashboardUrl)
            ? dashboardOpenStatus
            : string.IsNullOrWhiteSpace(reporter.LastDashboardUrl) ? "Dashboard link derived from endpoint." : dashboardOpenStatus;
        ImGui.TextColored(GetDashboardStatusColor(status), status);
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
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            dashboardOpenStatus = "Opened dashboard in external browser.";
        }
        catch (Exception ex)
        {
            dashboardOpenStatus = $"Unable to open dashboard. {ex.Message}";
            log.Error(ex, "[MarketMafioso] Unable to open dashboard URL.");
        }
    }

    private void ApplyServerUrlPreset(string value)
    {
        urlBuffer = value;
        config.ServerUrl = value;
        config.Save();
    }

    private static Vector4 GetDashboardStatusColor(string status) =>
        status.StartsWith("Unable", StringComparison.OrdinalIgnoreCase) || status.Contains("not a valid", StringComparison.OrdinalIgnoreCase)
            ? MarketMafiosoUiTheme.Error
            : MarketMafiosoUiTheme.Muted;
}

