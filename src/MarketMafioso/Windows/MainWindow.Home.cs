using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

public partial class MainWindow
{
    private const string ServerConnectionSettingsPageId = "general.server";

    private static readonly string[] MainTabNames =
    [
        HomeTabPanel.TabName,
        "Workshop Logistics",
        "Trade Queue",
        "Market Acquisition",
        "Diagnostics",
        "Settings",
        "Status",
    ];

    // Set when the window opens; selects the remembered tab on the next tab-bar frame.
    private string? restoreMainTab;
    private bool mainTabSelectionForced;

    private HomeTabPanel CreateHomeTab() =>
        new(
            config,
            AgentReviewRegistry,
            Plugin.PluginInterface,
            BuildHomeSnapshot,
            new HomeTabActions(
                OpenMainTabFromHome,
                ResumeWorkshopAssemblyFromHome,
                OpenServerConnectionSettings,
                OpenHomeUrl),
            log);

    public override void OnOpen()
    {
        base.OnOpen();
        // An explicit request (agent bridge, staging, Show Trade Queue) already chose the tab.
        if (agentRequestedTab is null)
            restoreMainTab = ResolveRestorableMainTab(config.LastMainTab);
    }

    private string ResolveRestorableMainTab(string? stored) =>
        stored is not null && MainTabNames.Contains(stored, StringComparer.Ordinal) && IsMainTabAvailable(stored)
            ? stored
            : HomeTabPanel.TabName;

    private bool IsMainTabAvailable(string mainTab) =>
        mainTab switch
        {
            "Market Acquisition" => acquisitionServices.IsMarketAcquisitionUnlocked(),
            _ => MainTabNames.Contains(mainTab, StringComparer.Ordinal),
        };

    /// <summary>Flags for one main tab item, honoring an explicit request first, then the open-time restore.</summary>
    private ImGuiTabItemFlags GetMainTabFlags(string tabName)
    {
        var requested = agentRequestedTab ?? restoreMainTab;
        if (requested is null)
            return ImGuiTabItemFlags.None;

        mainTabSelectionForced = true;
        return string.Equals(requested, tabName, StringComparison.Ordinal)
            ? ImGuiTabItemFlags.SetSelected
            : ImGuiTabItemFlags.None;
    }

    /// <summary>Called for the tab item ImGui reports open this frame; persists it once no selection is being forced.</summary>
    private void ObserveOpenMainTab(string tabName)
    {
        if (mainTabSelectionForced || string.Equals(config.LastMainTab, tabName, StringComparison.Ordinal))
            return;

        config.LastMainTab = tabName;
        config.Save();
    }

    private void EndMainTabFrame()
    {
        restoreMainTab = null;
        mainTabSelectionForced = false;
    }

    private void OpenMainTabFromHome(string mainTab)
    {
        if (!IsMainTabAvailable(mainTab))
            return;
        QueueAgentTabSelection(mainTab);
    }

    private void ResumeWorkshopAssemblyFromHome()
    {
        workshopStatus = workshopAssemblyRunner.Resume().Message;
        QueueAgentTabSelection("Workshop Logistics", "Assembly");
    }

    private void OpenServerConnectionSettings()
    {
        settingsTab.ShowPage(ServerConnectionSettingsPageId);
        QueueAgentTabSelection("Settings");
    }

    private void OpenHomeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            log.Warning($"[MarketMafioso] Home refused to open a non-HTTP link: {url}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[MarketMafioso] Home could not open {uri} in the browser.");
        }
    }

    /// <summary>
    /// Home's facts, read from the same snapshots and services the module tabs use. Rendering throttles this
    /// to once a second (<see cref="HomeTabPanel"/>), so it never runs per frame.
    /// </summary>
    private HomeSnapshot BuildHomeSnapshot() =>
        new(
            BuildHomeWorkshopFacts(),
            BuildHomeTradeFacts(),
            BuildHomeMarketFacts(),
            BuildHomeHostFacts(),
            CountRetainersSeen());

    private HomeWorkshopFacts BuildHomeWorkshopFacts()
    {
        var queue = config.WorkshopPrepQueue.Where(item => item.Quantity > 0).ToArray();
        var projectsById = workshopCatalog.GetProjects()
            .GroupBy(project => project.WorkshopItemId)
            .ToDictionary(group => group.Key, group => group.First().Name);
        var names = queue
            .Select(item => projectsById.GetValueOrDefault(item.WorkshopItemId))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var availability = GetWorkshopAvailability();
        var progress = workshopAssemblyRunner.Progress;
        return new HomeWorkshopFacts(
            queue.Length,
            queue.Sum(item => item.Quantity),
            names,
            availability.Count,
            availability.Count(item => item.Shortage > 0),
            workshopAssemblyRunner.IsRunning,
            workshopAssemblyRunner.IsPaused,
            progress.Message,
            progress.CompletedProjects,
            progress.TotalProjects);
    }

    private HomeTradeFacts BuildHomeTradeFacts()
    {
        var lines = config.TradeQueueItems.Where(item => item.Quantity > 0).ToArray();
        var execution = tradeQueueRunner.Snapshot;
        return new HomeTradeFacts(
            lines.Length,
            lines.Sum(item => item.Quantity),
            execution.IsActive && !string.IsNullOrWhiteSpace(execution.PartnerName)
                ? execution.PartnerName
                : config.TradeQueueBinding?.RecipientName,
            execution.IsActive,
            !execution.IsActive && tradeQueueRunner.HasResumeCheckpoint,
            tradeQueueRunner.HoldReason ?? execution.Message,
            execution.RemainingUnitCount);
    }

    private HomeMarketFacts BuildHomeMarketFacts()
    {
        var unlocked = acquisitionServices.IsMarketAcquisitionUnlocked();
        var hostStatus = Plugin.Instance.AcquisitionHostCheck.Status;
        var hostOffers = !hostStatus.StartsWith("Server does not offer", StringComparison.Ordinal);
        if (!unlocked)
            return new HomeMarketFacts(false, config.MarketAcquisitionLockedByUser, hostOffers, 0, 0, false, string.Empty, 0, 0, null, null, null, null, false);

        var route = routeEngine.CreateSnapshot();
        var plan = acquisitionWorkspace.PreparedPlan;
        var ready = plan is { Status: "Ready" };
        return new HomeMarketFacts(
            true,
            config.MarketAcquisitionLockedByUser,
            hostOffers,
            acquisitionRequestBuilder.LineCount,
            acquisitionWorkspace.PendingRequests.Count,
            route.IsRouteActive,
            route.VisibleAcquisitionStatus,
            route.Stops.Count,
            route.CompletedOrProbedStopCount,
            ready ? plan!.Lines.Count : null,
            ready ? plan!.WorldBatches.Count : null,
            ready ? plan!.PlannedGil : null,
            ready ? plan!.PreparedAtUtc : null,
            ready && acquisitionWorkspace.IsPreparedPlanStale());
    }

    private HomeHostFacts BuildHomeHostFacts()
    {
        var serverUrl = config.ServerUrl;
        var hostStatus = Plugin.Instance.AcquisitionHostCheck.Status;
        var lastSendOk = reporter.LastSentAt.HasValue && reporter.LastStatus.StartsWith('2');
        var hostReached = hostStatus is "Unlocked." or "Server offers Market Acquisition." ||
                          hostStatus.StartsWith("Server does not offer", StringComparison.Ordinal);
        var paired = !string.IsNullOrWhiteSpace(config.ApiKey);
        var connection = string.IsNullOrWhiteSpace(serverUrl)
            ? HomeHostConnection.NotConnected
            : lastSendOk || hostReached
                ? HomeHostConnection.Connected
                : hostStatus == "Checking..."
                    ? HomeHostConnection.Checking
                    // The default localhost receiver with no key is "never set up", not a fault.
                    : paired || reporter.LastSentAt.HasValue
                        ? HomeHostConnection.Unreachable
                        : HomeHostConnection.NotConnected;
        var hostLabel = Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ? uri.Authority : serverUrl;
        return new HomeHostFacts(
            connection,
            hostLabel,
            reporter.LastSentAt,
            lastSendOk,
            reporter.LastStatus,
            HttpReporter.ResolveDashboardUrlForDisplay(reporter.LastDashboardUrl, serverUrl),
            config.EnableAutoSendTimer,
            config.AutoSendIntervalMinutes);
    }

    private int? CountRetainersSeen()
    {
        var owner = acquisitionServices.GetCurrentQuartermasterOwnerScope();
        if (owner.LocalContentId is not > 0 || owner.HomeWorldId is not > 0)
            return null;

        return retainerReports.TryCountRetainers(
            new Franthropy.Observations.V1.ObservationOwner(owner.LocalContentId.Value, owner.HomeWorldId.Value),
            out var count)
            ? count
            : null;
    }
}
