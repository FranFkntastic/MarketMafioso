using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.UI.Settings;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class ServerConnectionSettingsPage
{
    private const string LocalReceiverUrl = "http://localhost:8080/inventory";
    private const string DevReceiverUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory";
    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly IPluginLog log;
    private readonly Func<MarketIntelligenceReportingStatus>? intelligenceStatus;
    private string urlBuffer;
    private string apiKeyBuffer;
    private string acquisitionApiKeyBuffer;
    private string dashboardUrlBuffer = string.Empty;
    private string dashboardOpenStatus = "Dashboard link appears after a successful send.";
    private bool showApiKey;
    private bool showAcquisitionApiKey;
    private string pairingCodeBuffer = string.Empty;
    private string pairingStatus = string.Empty;
    private bool pairingStatusIsError;
    private Task<WorkshopHostPairing.RedeemResult>? pendingPairing;
    private WorkshopHostPairing.ParsedCode pendingPairingCode;

    public ServerConnectionSettingsPage(Configuration config, HttpReporter reporter, IPluginLog log,
        Func<MarketIntelligenceReportingStatus>? intelligenceStatus = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.intelligenceStatus = intelligenceStatus;
        urlBuffer = config.ServerUrl;
        apiKeyBuffer = config.ApiKey;
        acquisitionApiKeyBuffer = config.CommandPickupApiKey;
        Descriptor = new SettingsPageDescriptor(
            "general.server",
            "General / Server Connection",
            Draw,
            0,
            searchTerms: ["connect with code", "connection code", "pairing code", "receiver URL", "API key", "MMF client key", "Craft Architect key", "dashboard", "local receiver", "development server", "market intelligence", "quarantine", "conflict", "pending reports"]);
    }

    public SettingsPageDescriptor Descriptor { get; }

    private void Draw(SettingsPageContext context)
    {
        CompletePairingIfFinished();
        var marketAcquisitionUnlocked = MarketAcquisitionUnlock.IsUnlocked(config);
        if (context.Matches("Connect with code", "connection code", "pairing code", "dashboard code"))
            DrawConnectWithCode();

        if (context.Matches("Server URL", "receiver endpoint", "local receiver", "development server",
                "Client API key", "MMF client key", "Craft Architect key", "acquisition key", "X-Api-Key", "authentication", "manual setup"))
        {
            if (context.IsFiltering)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if (ImGui.CollapsingHeader("Manual setup##serverManualSetup"))
                DrawManualSetup(context, marketAcquisitionUnlocked);
        }

        if (context.Matches("Dashboard URL", "open dashboard", "receiver dashboard"))
            DrawDashboard();
        DrawIntelligenceStatus(context);
    }

    private void DrawConnectWithCode()
    {
        ImGui.Text("Connect with code:");
        const float buttonWidth = 90f;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        var submitted = ImGui.InputTextWithHint(
            "##pairingCode",
            "Paste the code from the dashboard",
            ref pairingCodeBuffer,
            1024,
            ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var canConnect = pendingPairing is null && !string.IsNullOrWhiteSpace(pairingCodeBuffer);
        if ((ImGuiUi.PrimaryButton(pendingPairing is null ? "Connect##pairing" : "Connecting...##pairing", canConnect) || submitted) && canConnect)
            StartPairing();

        if (pairingStatus.Length > 0)
            ImGui.TextColored(pairingStatusIsError ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted, pairingStatus);
        ImGui.Spacing();
    }

    private void StartPairing()
    {
        if (!WorkshopHostPairing.TryParse(pairingCodeBuffer, out var code, out var error))
        {
            pairingStatus = error;
            pairingStatusIsError = true;
            return;
        }

        string? clientName = null;
        try
        {
            clientName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue;
        }
        catch (Exception ex)
        {
            log.Debug($"[MarketMafioso] Character name unavailable for the pairing label: {ex.Message}");
        }

        pendingPairingCode = code;
        pairingStatus = $"Connecting to {WorkshopHostPairing.DescribeHost(code.HostBaseUrl)}...";
        pairingStatusIsError = false;
        pendingPairing = Task.Run(() => WorkshopHostPairing.RedeemAsync(code, clientName, CancellationToken.None));
    }

    private void CompletePairingIfFinished()
    {
        if (pendingPairing is not { IsCompleted: true } task)
            return;

        pendingPairing = null;
        var host = WorkshopHostPairing.DescribeHost(pendingPairingCode.HostBaseUrl);
        WorkshopHostPairing.RedeemResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = new WorkshopHostPairing.RedeemResult(false, null, $"Connecting to {host} failed: {ex.Message}", "exception");
        }

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.ApiKey))
        {
            pairingStatus = result.Message;
            pairingStatusIsError = true;
            log.Warning($"[MarketMafioso] Connect with code to {host} failed ({result.ErrorCode ?? "unknown"}): {result.Message}");
            return;
        }

        // The issued client key covers every plugin route, Market Acquisition included, so an
        // acquisition-only key left over from earlier manual setup must not shadow it.
        config.ApiKey = result.ApiKey;
        config.CommandPickupApiKey = string.Empty;
        // Setting ServerUrl raises ServerUrlChanged, which runs the host feature check.
        config.ServerUrl = WorkshopHostPairing.BuildServerUrl(pendingPairingCode.HostBaseUrl);
        config.Save();
        urlBuffer = config.ServerUrl;
        apiKeyBuffer = config.ApiKey;
        acquisitionApiKeyBuffer = config.CommandPickupApiKey;
        pairingCodeBuffer = string.Empty;
        pairingStatus = result.Message;
        pairingStatusIsError = false;
        log.Information($"[MarketMafioso] Connected to Workshop Host {host} with a connection code.");
    }

    private void DrawManualSetup(SettingsPageContext context, bool marketAcquisitionUnlocked)
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
        if (context.Matches("Client API key", "MMF client key", "Craft Architect key", "acquisition key", "X-Api-Key", "authentication", "receiver endpoint"))
        {
            ImGui.Text(endpoint.RequiresApiKey
                ? "MarketMafioso Client Key (required for inventory uploads):"
                : "MarketMafioso Client Key (optional for this endpoint):");
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
            else if (WorkshopHostApiKeyRouting.IsCraftArchitectKey(apiKeyBuffer))
                ImGui.TextColored(
                    MarketMafiosoUiTheme.Error,
                    marketAcquisitionUnlocked
                        ? "This is a Craft Architect key. It cannot upload inventory and belongs in the Acquisition Key field below."
                        : "This is a Craft Architect key and cannot upload inventory.");
            else if (endpoint.RequiresApiKey && string.IsNullOrWhiteSpace(apiKeyBuffer))
                ImGui.TextColored(MarketMafiosoUiTheme.Error, "This endpoint requires a MarketMafioso Client Key before inventory can be sent.");
            else if (endpoint.Kind == ReceiverEndpointKind.CustomRemote)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Custom remote endpoint. A client key is required by default.");

            if (marketAcquisitionUnlocked && WorkshopHostApiKeyRouting.IsCraftArchitectKey(apiKeyBuffer))
            {
                var canMove = string.IsNullOrWhiteSpace(acquisitionApiKeyBuffer);
                if (ImGuiUi.Button("Move to Acquisition Key", new Vector2(190, 0), canMove))
                {
                    acquisitionApiKeyBuffer = apiKeyBuffer;
                    apiKeyBuffer = string.Empty;
                    config.CommandPickupApiKey = acquisitionApiKeyBuffer;
                    config.ApiKey = string.Empty;
                    config.Save();
                }

                if (!canMove)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Clear the existing Acquisition Key before moving this one.");
                }
            }

            if (marketAcquisitionUnlocked)
            {
                ImGui.Spacing();
                ImGui.Text("Craft Architect Acquisition Key (optional):");
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 70);
                var acquisitionFlags = showAcquisitionApiKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
                if (ImGui.InputText("##acquisitionApiKey", ref acquisitionApiKeyBuffer, 256, acquisitionFlags))
                {
                    config.CommandPickupApiKey = acquisitionApiKeyBuffer;
                    config.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button(showAcquisitionApiKey ? "Hide##ak" : "Show##ak", new Vector2(60, 0))) showAcquisitionApiKey = !showAcquisitionApiKey;

                if (string.IsNullOrWhiteSpace(acquisitionApiKeyBuffer))
                    ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Market Acquisition will use the full MarketMafioso Client Key above.");
                else if (WorkshopHostApiKeyRouting.IsMarketMafiosoClientKey(acquisitionApiKeyBuffer))
                    ImGui.TextColored(MarketMafiosoUiTheme.Muted, "A full MarketMafioso Client Key is valid here, but leaving this blank uses the key above automatically.");
                else
                    ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Used only for Market Acquisition queue, claim, progress, and audit requests.");
            }

            var keyManagerUrl = ReceiverEndpointClassifier.BuildClientKeyManagerUrl(urlBuffer);
            if (endpoint.RequiresApiKey && !string.IsNullOrWhiteSpace(keyManagerUrl))
            {
                ImGui.TextColored(
                    MarketMafiosoUiTheme.Muted,
                    marketAcquisitionUnlocked
                        ? "Both key types are issued and revoked in the authenticated server dashboard."
                        : "Client keys are issued and revoked in the authenticated server dashboard.");
                if (ImGui.Button("Manage Client Keys")) OpenExternalUrl(keyManagerUrl, "client key manager");
            }
            ImGui.Spacing();
        }
    }

    private void DrawIntelligenceStatus(SettingsPageContext context)
    {
        if (intelligenceStatus is not null && context.Matches("Market intelligence", "quarantine", "conflict", "pending reports"))
        {
            var status = intelligenceStatus();
            ImGui.Spacing();
            ImGui.Text("Market intelligence reports");
            ImGui.Text($"Pending: {status.PendingCount} | Quarantined conflicts: {status.QuarantinedCount}");
            if (status.QuarantinedCount > 0)
            {
                ImGui.TextColored(MarketMafiosoUiTheme.Error, "Conflicting reports are preserved and require reconciliation.");
                ImGui.TextWrapped("Other pending reports continue uploading. Quarantined reports are not retried automatically.");
                ImGui.TextWrapped(status.QuarantineReason ?? string.Empty);
                if (status.LastQuarantinedAtUtc is { } last)
                    ImGui.Text($"Last conflict: {last:yyyy-MM-dd HH:mm:ss} UTC");
            }
        }
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
        if (ImGuiUi.Button("Open Dashboard", new Vector2(buttonWidth, 0), !string.IsNullOrWhiteSpace(dashboardUrl))) OpenExternalUrl(dashboardUrl, "dashboard");
        var status = string.IsNullOrWhiteSpace(dashboardUrl)
            ? dashboardOpenStatus
            : string.IsNullOrWhiteSpace(reporter.LastDashboardUrl) ? "Dashboard link derived from endpoint." : dashboardOpenStatus;
        ImGui.TextColored(GetDashboardStatusColor(status), status);
    }

    private void OpenExternalUrl(string url, string destination)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            dashboardOpenStatus = $"The {destination} URL is not a valid HTTP or HTTPS link.";
            log.Warning($"[MarketMafioso] Refusing to open invalid {destination} URL: {url}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            dashboardOpenStatus = $"Opened {destination} in external browser.";
        }
        catch (Exception ex)
        {
            dashboardOpenStatus = $"Unable to open {destination}. {ex.Message}";
            log.Error(ex, $"[MarketMafioso] Unable to open {destination} URL.");
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

