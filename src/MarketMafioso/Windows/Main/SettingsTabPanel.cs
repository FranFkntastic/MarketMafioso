using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Settings;
using MarketMafioso.Windows.Main.Settings;
using MarketMafioso.Squire;

namespace MarketMafioso.Windows.Main;

internal sealed class SettingsTabPanel
{
    private const string DefaultPageId = "general.server";
    private readonly Configuration config;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly SettingsNavigationState navigationState;
    private readonly SettingsNavigationCatalog navigationCatalog;
    private readonly DalamudSettingsTreeRenderer navigationRenderer = new("MarketMafioso");

    public SettingsTabPanel(
        Configuration config,
        HttpReporter reporter,
        AutoRetainerRefreshService autoRetainerRefresh,
        IPluginLog log,
        Action stopMarketAcquisitionRoute,
        Action restartTimer,
        IPlayerState playerState,
        IDataManager dataManager,
        Func<SquireAnalysis?> currentSquireAnalysis,
        Action requestSquirePolicyRefresh,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        navigationState = new SettingsNavigationState(
            string.IsNullOrWhiteSpace(config.SettingsSelectedPageId) ? DefaultPageId : config.SettingsSelectedPageId,
            config.SettingsExpandedFolderPaths);

        var pages = new List<SettingsPageDescriptor>
        {
            new ServerConnectionSettingsPage(config, reporter, log).Descriptor,
        };
        pages.AddRange(new InventoryReporterSettingsPages(config, restartTimer, reporter, autoRetainerRefresh).Descriptors);
        pages.AddRange(new SquireSettingsPages(config, playerState, dataManager, currentSquireAnalysis, requestSquirePolicyRefresh, reviewRegistry).Descriptors);
        pages.AddRange(new MarketAcquisitionSettingsPages(config).Descriptors);
        pages.AddRange(new AdvancedSettingsPages(config, stopMarketAcquisitionRoute).Descriptors);
        navigationCatalog = new SettingsNavigationCatalog(pages);
    }

    public void Draw()
    {
        Dalamud.Bindings.ImGui.ImGui.Spacing();
        Dalamud.Bindings.ImGui.ImGui.TextColored(MarketMafiosoUiTheme.Header, "Plugin Settings");
        Dalamud.Bindings.ImGui.ImGui.TextWrapped("Choose a module on the left. Search finds page names, setting labels, and supporting explanations.");
        Dalamud.Bindings.ImGui.ImGui.Spacing();

        navigationRenderer.Draw(navigationCatalog, navigationState, PersistNavigation);
        RegisterRenderedControls();
    }

    private void RegisterRenderedControls()
    {
        foreach (var folder in navigationRenderer.RenderedFolderControls)
        {
            reviewRegistry.Register(
                $"settings.folder.{CreateControlId(folder.Path)}",
                $"{(folder.Expanded ? "Collapse" : "Expand")} settings folder: {folder.Label}",
                AgentBridgeUiControlKind.Toggle,
                folder.Min,
                folder.Max,
                true,
                folder.Expanded,
                folder.Expanded ? "Expanded" : "Collapsed",
                () => SetFolderExpanded(folder.Path, !folder.Expanded));
        }

        foreach (var page in navigationRenderer.RenderedPageControls)
        {
            reviewRegistry.Register(
                $"settings.page.{page.Id}",
                $"Open settings page: {page.Label}",
                AgentBridgeUiControlKind.Select,
                page.Min,
                page.Max,
                true,
                page.Selected,
                page.Label,
                () => SelectPage(page.Id));
        }
    }

    private void SelectPage(string pageId)
    {
        navigationState.SelectPage(pageId);
        PersistNavigation();
    }

    private void SetFolderExpanded(string path, bool expanded)
    {
        navigationState.SetFolderExpanded(path, expanded);
        PersistNavigation();
    }

    private void PersistNavigation()
    {
        config.SettingsSelectedPageId = navigationState.SelectedPageId ?? DefaultPageId;
        config.SettingsExpandedFolderPaths = navigationState.ExpandedFolderPaths.OrderBy(path => path, StringComparer.Ordinal).ToList();
        config.Save();
    }

    private static string CreateControlId(string value) => new(value
        .ToLowerInvariant()
        .Select(character => char.IsLetterOrDigit(character) ? character : '-')
        .ToArray());
}
