using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Performance;
using Franthropy.Dalamud.UI.Styling;

namespace MarketMafioso.Windows.Main;

internal enum HomeHostConnection
{
    NotConnected,
    Checking,
    Unreachable,
    Connected,
}

internal sealed record HomeWorkshopFacts(
    int ProjectCount,
    int BuildCount,
    IReadOnlyList<string> ProjectNames,
    int MaterialCount,
    int ShortCount,
    bool AssemblyRunning,
    bool AssemblyPaused,
    string AssemblyMessage,
    int CompletedProjects,
    int TotalProjects);

internal sealed record HomeTradeFacts(
    int LineCount,
    int UnitCount,
    string? RecipientName,
    bool RunActive,
    bool CanResume,
    string RunMessage,
    int RemainingUnits);

internal sealed record HomeMarketFacts(
    bool Unlocked,
    bool LockedByUser,
    bool HostOffersAcquisition,
    int BuyListLines,
    int InboxCount,
    bool RouteActive,
    string RouteStatus,
    int RouteStops,
    int RouteStopsDone,
    int? PlanLines,
    int? PlanWorlds,
    uint? PlanGil,
    DateTimeOffset? PlanPreparedAt,
    bool PlanStale);

internal sealed record HomeHostFacts(
    HomeHostConnection Connection,
    string HostLabel,
    DateTime? LastSentAt,
    bool LastSendOk,
    string LastStatus,
    string? DashboardUrl,
    bool AutoSend,
    int AutoSendMinutes);

internal sealed record HomeSnapshot(
    HomeWorkshopFacts Workshop,
    HomeTradeFacts Trade,
    HomeMarketFacts Market,
    HomeHostFacts Host,
    int? RetainersSeen);

internal sealed record HomeTabActions(
    Action<string> OpenTab,
    Action ResumeAssembly,
    Action OpenServerSettings,
    Action<string> OpenUrl);

/// <summary>
/// The Home landing tab: one card per module with live facts, the Workshop Host and companion-plugin
/// state, and a getting-started strip that collapses once the queue and retainer stock are known.
/// </summary>
internal sealed class HomeTabPanel : IDisposable
{
    public const string TabName = "Home";
    public const string InstallGuideUrl =
        "https://github.com/FranFkntastic/MarketMafioso/blob/main/docs/installation.md#download-the-workshop-host-package";

    private const float SideColumnWidth = 300f;

    private static readonly CompanionPlugin[] Companions =
    [
        new("vnavmesh", "vnavmesh", "Walking routes for buying runs"),
        new("Lifestream", "Lifestream", "World travel during buying runs"),
        new("TextAdvance", "TextAdvance", "Skips workshop cutscenes"),
        new("Quartermaster", "RQ", "Pulls workshop materials from retainers"),
        new("VIWI Workshoppa", "VIWI", "Alternative workshop queue runner"),
    ];

    private static readonly Vector4 TextColor = new(0.92f, 0.92f, 0.92f, 1f);
    private static readonly Vector4 DescriptionColor = new(0.77f, 0.77f, 0.77f, 1f);
    private static readonly DalamudUiPalette Palette = new(
        MarketMafiosoUiTheme.Header,
        MarketMafiosoUiTheme.Success,
        MarketMafiosoUiTheme.Warning,
        MarketMafiosoUiTheme.Error,
        TextColor,
        MarketMafiosoUiTheme.Muted,
        new Vector4(0.08f, 0.08f, 0.08f, 0.94f),
        new Vector4(0.13f, 0.13f, 0.13f, 0.96f),
        new Vector4(0.23f, 0.24f, 0.26f, 1f));

    private readonly Configuration config;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly HomeTabActions actions;
    private readonly CadencedProbe<HomeSnapshot> snapshotProbe;
    private readonly CadencedProbe<bool[]> companionProbe = new(
        TimeSpan.FromSeconds(5),
        "Home 'Works with' plugin load state; refreshed on a cadence and on Dalamud's plugin-change event.");
    private readonly Func<HomeSnapshot> buildSnapshot;
    private readonly IPluginLog log;
    private string? lastSnapshotFailure;

    // Dalamud's ImGui bindings have no auto-resizing child windows, so each card is sized from the content
    // height it measured on the previous frame (the first frame uses an estimate).
    private readonly Dictionary<string, float> measuredCardHeights = new(StringComparer.Ordinal);
    private string? currentCardId;
    private bool currentCardMeasured;

    public HomeTabPanel(
        Configuration config,
        AgentBridgeUiReviewRegistry reviewRegistry,
        IDalamudPluginInterface pluginInterface,
        Func<HomeSnapshot> buildSnapshot,
        HomeTabActions actions,
        IPluginLog log)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.buildSnapshot = buildSnapshot ?? throw new ArgumentNullException(nameof(buildSnapshot));
        this.actions = actions ?? throw new ArgumentNullException(nameof(actions));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        snapshotProbe = new CadencedProbe<HomeSnapshot>(
            TimeSpan.FromSeconds(1),
            "Home module facts aggregate the same snapshots the module tabs read; once a second is plenty for a summary.");
        pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
    }

    public void Dispose() => pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => companionProbe.Invalidate();

    public void Draw()
    {
        var snapshot = snapshotProbe.Read(BuildSnapshotObserved, RecoverSnapshot).Value;
        var companions = companionProbe.Read(ProbeCompanions, exception =>
        {
            log.Warning(exception, "[MarketMafioso] Home could not read the installed-plugin list; companions show as not installed.");
            return new bool[Companions.Length];
        }).Value;

        if (!ImGui.BeginChild("##mmfHome", Vector2.Zero, false))
        {
            ImGui.EndChild();
            return;
        }

        ImGui.Spacing();
        if (ShouldShowGettingStarted(snapshot))
        {
            DrawGettingStarted(snapshot);
            ImGui.Spacing();
        }

        if (ImGui.BeginTable("##mmfHomeLayout", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoPadOuterX))
        {
            ImGui.TableSetupColumn("Modules", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Side", ImGuiTableColumnFlags.WidthFixed, SideColumnWidth);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawModuleCards(snapshot);
            ImGui.TableNextColumn();
            DrawHostCard(snapshot.Host);
            ImGui.Spacing();
            DrawCompanionsCard(companions);
            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private HomeSnapshot BuildSnapshotObserved()
    {
        var snapshot = buildSnapshot();
        if (lastSnapshotFailure is not null)
        {
            log.Information("[MarketMafioso] Home module facts recovered after: {Failure}", lastSnapshotFailure);
            lastSnapshotFailure = null;
        }

        return snapshot;
    }

    /// <summary>Keeps the last good facts on screen and logs each distinct failure once, not once a second.</summary>
    private HomeSnapshot RecoverSnapshot(Exception exception)
    {
        var failure = $"{exception.GetType().Name}: {exception.Message}";
        if (!string.Equals(failure, lastSnapshotFailure, StringComparison.Ordinal))
        {
            log.Warning(exception, "[MarketMafioso] Home module facts failed to refresh; showing the last good facts.");
            lastSnapshotFailure = failure;
        }

        return snapshotProbe.HasValue ? snapshotProbe.Value : EmptySnapshot;
    }

    private bool ShouldShowGettingStarted(HomeSnapshot snapshot)
    {
        if (config.HomeGettingStartedHidden)
            return false;

        // Queues empty again after a handoff or a run, so settling is remembered rather than re-evaluated.
        if (IsGettingStartedSettled(snapshot))
        {
            HideGettingStarted();
            return false;
        }

        return true;
    }

    /// <summary>Settled once something is in the Trade Queue and a buy list has been started.</summary>
    internal static bool IsGettingStartedSettled(HomeSnapshot snapshot) =>
        IsHandoffStarted(snapshot) && IsBuyListStarted(snapshot);

    private static bool IsHandoffStarted(HomeSnapshot snapshot) => snapshot.Trade.LineCount > 0 || snapshot.Trade.RunActive;

    private static bool IsBuyListStarted(HomeSnapshot snapshot) =>
        snapshot.Market.BuyListLines > 0 || snapshot.Market.PlanLines is not null || snapshot.Market.RouteActive;

    private void DrawGettingStarted(HomeSnapshot snapshot)
    {
        const string id = "##mmfHomeGettingStarted";
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var height = CardHeight((lineHeight * 7f) + ImGui.GetFrameHeightWithSpacing(), id);
        var start = ImGui.GetCursorScreenPos();
        if (BeginCard(id, height))
        {
            ImGui.TextColored(TextColor, "Getting started");
            ImGuiUi.SameLineRight(ButtonWidth("Hide"));
            if (ImGui.SmallButton("Hide##homeGettingStarted"))
                HideGettingStarted();
            RegisterLast("home.getting-started.hide", "Hide the Getting started steps", HideGettingStarted);

            // Three equal columns laid out by hand so every step header shares one baseline.
            var spacing = ImGui.GetStyle().ItemSpacing.X * 3f;
            var origin = ImGui.GetCursorPos();
            var columnWidth = (ImGui.GetContentRegionAvail().X - (spacing * 2f)) / 3f;
            var bottom = origin.Y;
            for (var step = 1; step <= 3; step++)
            {
                var x = origin.X + ((step - 1) * (columnWidth + spacing));
                ImGui.SetCursorPos(new Vector2(x, origin.Y));
                ImGui.BeginGroup();
                ImGui.PushTextWrapPos(x + columnWidth);
                switch (step)
                {
                    case 1:
                        DrawStep(
                            1,
                            "Line up a handoff",
                            "Add items to the Trade Queue and pick who gets them. When you open a trade with that player, MMF fills the window and tracks what's left.",
                            IsHandoffStarted(snapshot) ? $"✓ {Plural(snapshot.Trade.LineCount, "item")} queued" : null,
                            () =>
                            {
                                if (ImGui.Button("Open Trade Queue##homeStep1"))
                                    actions.OpenTab("Trade Queue");
                                RegisterLast("home.getting-started.trade", "Open Trade Queue", () => actions.OpenTab("Trade Queue"));
                            });
                        break;
                    case 2:
                        DrawStep(
                            2,
                            "Build a buy list",
                            "List what you need and the most you'll pay. MMF finds the cheapest worlds, plans the route, and buys under your ceiling.",
                            IsBuyListStarted(snapshot) ? "✓ Buy list started" : null,
                            () =>
                            {
                                if (snapshot.Market.Unlocked)
                                {
                                    if (ImGui.Button("Open Market Acquisition##homeStep2"))
                                        actions.OpenTab("Market Acquisition");
                                    RegisterLast("home.getting-started.market", "Open Market Acquisition", () => actions.OpenTab("Market Acquisition"));
                                }
                                else
                                {
                                    if (ImGui.Button("Set up Workshop Host##homeStep2"))
                                        actions.OpenServerSettings();
                                    RegisterLast("home.getting-started.market-host", "Open Settings > Server Connection to set up a Workshop Host", actions.OpenServerSettings);
                                }
                            });
                        break;
                    default:
                        DrawStep(
                            3,
                            "Optional: connect a Workshop Host",
                            "A self-hosted helper that unlocks Market Acquisition and keeps inventory history. Everything else works without it.",
                            snapshot.Host.Connection == HomeHostConnection.Connected ? "✓ Connected" : null,
                            () =>
                            {
                                if (ImGui.Button("Connect with code##homeStep3"))
                                    actions.OpenServerSettings();
                                RegisterLast("home.getting-started.connect", "Open Settings > Server Connection to connect with a code", actions.OpenServerSettings);
                            });
                        break;
                }

                ImGui.PopTextWrapPos();
                ImGui.EndGroup();
                bottom = MathF.Max(bottom, ImGui.GetCursorPosY());
            }

            ImGui.SetCursorPos(new Vector2(origin.X, bottom));
            ImGui.Dummy(Vector2.Zero);
        }

        EndCard();
        ImGui.GetWindowDrawList().AddRectFilled(
            start,
            new Vector2(start.X + 3f, start.Y + height),
            ImGui.GetColorU32(MarketMafiosoUiTheme.Header));
    }

    private static void DrawStep(int number, string title, string body, string? done, Action? drawAction)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, number.ToString());
        ImGui.SameLine();
        ImGui.TextColored(TextColor, title);
        ImGui.PushStyleColor(ImGuiCol.Text, DescriptionColor);
        ImGui.TextUnformatted(body);
        ImGui.PopStyleColor();
        if (done is not null)
            ImGui.TextColored(MarketMafiosoUiTheme.Success, done);
        else
            drawAction?.Invoke();
    }

    private void HideGettingStarted()
    {
        config.HomeGettingStartedHidden = true;
        config.Save();
    }

    private void DrawModuleCards(HomeSnapshot snapshot)
    {
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var estimate = (lineHeight * 6f) + ImGui.GetFrameHeightWithSpacing();
        var topRowHeight = CardHeight(estimate, "##homeTrade", "##homeMarket");
        var bottomRowHeight = CardHeight(estimate, "##homeWorkshop", "##homeInventory");
        if (!ImGui.BeginTable("##mmfHomeCards", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX))
            return;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawTradeCard(snapshot.Trade, topRowHeight);
        ImGui.TableNextColumn();
        DrawMarketCard(snapshot.Market, snapshot.Host, topRowHeight);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawWorkshopCard(snapshot.Workshop, bottomRowHeight);
        ImGui.TableNextColumn();
        DrawInventoryCard(snapshot.Host, snapshot.RetainersSeen, bottomRowHeight);
        ImGui.EndTable();
    }

    private void DrawWorkshopCard(HomeWorkshopFacts workshop, float height)
    {
        if (BeginCard("##homeWorkshop", height))
        {
            var (pill, tone) = workshop switch
            {
                { AssemblyRunning: true } => ("Assembling", DalamudUiTone.Accent),
                { AssemblyPaused: true } => ("Assembly paused", DalamudUiTone.Warning),
                { ProjectCount: 0 } => ("Nothing queued", DalamudUiTone.Neutral),
                { ShortCount: > 0 } => ($"{workshop.ShortCount:N0} short", DalamudUiTone.Warning),
                { MaterialCount: > 0 } => ("All covered", DalamudUiTone.Success),
                _ => ("Queued", DalamudUiTone.Neutral),
            };
            DrawCardTitle("Workshop Logistics", pill, tone);
            DrawDescription("Queue company workshop projects, see which materials you're short across bags and retainers, pull them out, and run assembly at the workshop.");

            if (workshop.ProjectCount > 0)
            {
                DrawFact($"{Plural(workshop.ProjectCount, "project")} · {DescribeNames(workshop.ProjectNames, workshop.ProjectCount)}");
                if (workshop.MaterialCount > 0)
                {
                    if (workshop.ShortCount > 0)
                        DrawFactParts(
                            ($"{workshop.ShortCount:N0} of {workshop.MaterialCount:N0} materials short", MarketMafiosoUiTheme.Warning),
                            ($" · {workshop.MaterialCount - workshop.ShortCount:N0} covered", TextColor));
                    else
                        DrawFact($"All {workshop.MaterialCount:N0} materials covered", MarketMafiosoUiTheme.Success);
                }
            }

            if (workshop.AssemblyRunning || workshop.AssemblyPaused)
                DrawFact(
                    workshop.TotalProjects > 0
                        ? $"Assembly: {Math.Clamp(workshop.CompletedProjects, 0, workshop.TotalProjects)} of {workshop.TotalProjects} projects done"
                        : $"Assembly: {workshop.AssemblyMessage}",
                    workshop.AssemblyPaused ? MarketMafiosoUiTheme.Warning : TextColor);
            else if (workshop.ProjectCount > 0)
                DrawFact("Assembly idle", MarketMafiosoUiTheme.Muted);

            BeginCardFooter(height);
            if (ImGui.Button("Open Workshop Logistics##home"))
                actions.OpenTab("Workshop Logistics");
            RegisterLast("home.workshop.open", "Open Workshop Logistics", () => actions.OpenTab("Workshop Logistics"));
            if (workshop.AssemblyPaused)
            {
                ImGui.SameLine();
                if (SecondaryButton("Resume assembly##home"))
                    actions.ResumeAssembly();
                RegisterLast("home.workshop.resume-assembly", "Resume Workshop assembly", actions.ResumeAssembly);
            }
        }

        EndCard();
    }

    private void DrawTradeCard(HomeTradeFacts trade, float height)
    {
        if (BeginCard("##homeTrade", height))
        {
            var (pill, tone) = trade switch
            {
                { RunActive: true } => ("Trading", DalamudUiTone.Accent),
                { CanResume: true } => ("Paused", DalamudUiTone.Warning),
                { LineCount: 0 } => ("Empty", DalamudUiTone.Neutral),
                _ => ($"{Plural(trade.LineCount, "item")} waiting", DalamudUiTone.Accent),
            };
            DrawCardTitle("Trade Queue", pill, tone);
            DrawDescription("Line up items to hand to another player. When you open a trade with them, MMF fills the window for you and keeps track of what's left.");

            if (trade.LineCount > 0)
            {
                var units = $"{Plural(trade.LineCount, "item")} · {trade.UnitCount:N0} units";
                DrawFact(string.IsNullOrWhiteSpace(trade.RecipientName) ? units : $"{trade.RecipientName} · {units}");
            }

            if (trade.RunActive)
                DrawFact($"{trade.RemainingUnits:N0} units left in this handoff");
            else if (trade.CanResume)
                DrawFact(trade.RunMessage, MarketMafiosoUiTheme.Warning);

            BeginCardFooter(height);
            if (ImGui.Button("Open Trade Queue##home"))
                actions.OpenTab("Trade Queue");
            RegisterLast("home.trade.open", "Open Trade Queue", () => actions.OpenTab("Trade Queue"));
        }

        EndCard();
    }

    private void DrawMarketCard(HomeMarketFacts market, HomeHostFacts host, float height)
    {
        if (BeginCard("##homeMarket", height))
        {
            var (pill, tone) = market switch
            {
                { Unlocked: false } => (host.Connection == HomeHostConnection.Connected && !market.HostOffersAcquisition
                    ? "Not offered by host"
                    : market.LockedByUser && market.HostOffersAcquisition ? "Locked" : "Needs Workshop Host", DalamudUiTone.Neutral),
                { RouteActive: true } => ("Route running", DalamudUiTone.Accent),
                { PlanLines: not null, PlanStale: false } => ("Plan ready", DalamudUiTone.Success),
                { BuyListLines: > 0 } => ("Buy list open", DalamudUiTone.Accent),
                _ => ("Ready", DalamudUiTone.Neutral),
            };
            DrawCardTitle("Market Acquisition", pill, tone);
            DrawDescription("Turn a shopping list into a buying run. MMF compares listings across worlds, plans the route, travels, and buys under the price ceiling you set.");

            if (market.Unlocked)
            {
                if (market.RouteActive)
                {
                    DrawFact(market.RouteStops > 0
                        ? $"Route: {market.RouteStopsDone:N0} of {Plural(market.RouteStops, "world")} done"
                        : "Route running");
                    if (!string.IsNullOrWhiteSpace(market.RouteStatus))
                        DrawFact(market.RouteStatus, MarketMafiosoUiTheme.Muted);
                }
                else if (market.PlanLines is { } planLines)
                {
                    DrawFact($"Plan: {Plural(planLines, "item")} · {Plural(market.PlanWorlds ?? 0, "world")}");
                    var age = market.PlanPreparedAt is { } preparedAt ? DescribeAge(preparedAt) : null;
                    DrawFactParts(
                        ($"Planned {market.PlanGil ?? 0:N0} gil", TextColor),
                        (age is null ? string.Empty : $" · prices {age}", MarketMafiosoUiTheme.Muted),
                        (market.PlanStale ? " · buy list changed" : string.Empty, MarketMafiosoUiTheme.Warning));
                }
                else if (market.BuyListLines > 0)
                {
                    DrawFact($"Buy list: {Plural(market.BuyListLines, "item")}, not finalized");
                }

                if (market.InboxCount > 0)
                    DrawFact($"Inbox: {Plural(market.InboxCount, "work order")}");
            }

            BeginCardFooter(height);
            if (market.Unlocked)
            {
                if (ImGui.Button("Open Market Acquisition##home"))
                    actions.OpenTab("Market Acquisition");
                RegisterLast("home.market.open", "Open Market Acquisition", () => actions.OpenTab("Market Acquisition"));
            }
            else
            {
                if (SecondaryButton("Set up Workshop Host##homeMarket"))
                    actions.OpenServerSettings();
                RegisterLast("home.market.setup-host", "Open Settings > Server Connection to set up a Workshop Host", actions.OpenServerSettings);
            }
        }

        EndCard();
    }

    private void DrawInventoryCard(HomeHostFacts host, int? retainersSeen, float height)
    {
        if (BeginCard("##homeInventory", height))
        {
            var connected = host.Connection == HomeHostConnection.Connected;
            var (pill, tone) = host switch
            {
                { Connection: HomeHostConnection.Unreachable } => ("Host unreachable", DalamudUiTone.Warning),
                { Connection: not HomeHostConnection.Connected } => ("Needs Workshop Host", DalamudUiTone.Neutral),
                { LastSentAt: not null, LastSendOk: false } => ("Upload failed", DalamudUiTone.Warning),
                { LastSentAt: not null } => ("Synced", DalamudUiTone.Success),
                _ => ("Connected", DalamudUiTone.Neutral),
            };
            DrawCardTitle("Inventory History", pill, tone);
            DrawDescription("Your Workshop Host keeps snapshots of every character and retainer, so you can search what you own from a browser, even offline.");

            if (connected || host.Connection == HomeHostConnection.Unreachable)
            {
                if (host.LastSentAt is { } sentAt)
                    DrawFact(
                        host.LastSendOk
                            ? $"Last snapshot {DescribeAge(new DateTimeOffset(sentAt))}"
                            : $"Last upload failed: {host.LastStatus}",
                        host.LastSendOk ? TextColor : MarketMafiosoUiTheme.Warning);
                else
                    DrawFact("No snapshot sent this session", MarketMafiosoUiTheme.Muted);
                DrawFact(
                    host.AutoSend ? $"Auto-send every {host.AutoSendMinutes:N0} min" : "Auto-send off",
                    MarketMafiosoUiTheme.Muted);
                if (retainersSeen is > 0)
                    DrawFact($"{Plural(retainersSeen.Value, "retainer")} known for this character", MarketMafiosoUiTheme.Muted);
            }

            BeginCardFooter(height);
            if (connected && !string.IsNullOrWhiteSpace(host.DashboardUrl))
            {
                if (SecondaryButton("Open dashboard##homeInventory"))
                    actions.OpenUrl(host.DashboardUrl);
                RegisterLast("home.inventory.open-dashboard", "Open the Workshop Host dashboard in the browser", () => actions.OpenUrl(host.DashboardUrl));
            }
            else
            {
                if (SecondaryButton("Set up Workshop Host##homeInventory"))
                    actions.OpenServerSettings();
                RegisterLast("home.inventory.setup-host", "Open Settings > Server Connection to set up a Workshop Host", actions.OpenServerSettings);
            }
        }

        EndCard();
    }

    private void DrawHostCard(HomeHostFacts host)
    {
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var connected = host.Connection == HomeHostConnection.Connected;
        var height = CardHeight((lineHeight * (connected ? 3.4f : 5.4f)) + ImGui.GetFrameHeightWithSpacing(), "##homeHost");
        if (BeginCard("##homeHost", height))
        {
            DrawSubheading("Workshop Host");
            switch (host.Connection)
            {
                case HomeHostConnection.Connected:
                    DrawStatusLine(MarketMafiosoUiTheme.Success, "Connected");
                    DrawMutedLine(host.LastSentAt is { } sentAt
                        ? $"{host.HostLabel} · synced {DescribeAge(new DateTimeOffset(sentAt))}"
                        : host.HostLabel);
                    break;
                case HomeHostConnection.Unreachable:
                    DrawStatusLine(MarketMafiosoUiTheme.Warning, "Can't reach host");
                    DrawMutedLine(host.HostLabel);
                    break;
                case HomeHostConnection.Checking:
                    DrawStatusLine(MarketMafiosoUiTheme.Muted, "Checking...");
                    DrawMutedLine(host.HostLabel);
                    break;
                default:
                    DrawStatusLine(MarketMafiosoUiTheme.Muted, "Not connected");
                    DrawDescription("Optional. Run it on your PC or a server to unlock Market Acquisition and Inventory History.");
                    break;
            }

            BeginCardFooter(height);
            if (connected && !string.IsNullOrWhiteSpace(host.DashboardUrl))
            {
                if (SecondaryButton("Open dashboard##homeHost"))
                    actions.OpenUrl(host.DashboardUrl);
                RegisterLast("home.host.open-dashboard", "Open the Workshop Host dashboard in the browser", () => actions.OpenUrl(host.DashboardUrl));
            }
            else
            {
                if (SecondaryButton("Connect with code##homeHost"))
                    actions.OpenServerSettings();
                RegisterLast("home.host.connect", "Open Settings > Server Connection to connect with a code", actions.OpenServerSettings);
                ImGui.SameLine();
                if (SecondaryButton("Install guide##homeHost"))
                    actions.OpenUrl(InstallGuideUrl);
                RegisterLast("home.host.install-guide", "Open the Workshop Host installation guide in the browser", () => actions.OpenUrl(InstallGuideUrl));
            }
        }

        EndCard();
    }

    private void DrawCompanionsCard(bool[] loaded)
    {
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var height = CardHeight((lineHeight * ((Companions.Length * 2) + 1.6f)) + ImGui.GetStyle().WindowPadding.Y, "##homeCompanions");
        if (BeginCard("##homeCompanions", height))
        {
            DrawSubheading("Works with");
            for (var index = 0; index < Companions.Length; index++)
            {
                var companion = Companions[index];
                var isLoaded = index < loaded.Length && loaded[index];
                var state = isLoaded ? "✓ Loaded" : "Not installed";
                ImGui.TextColored(isLoaded ? TextColor : MarketMafiosoUiTheme.Muted, companion.DisplayName);
                ImGuiUi.SameLineRight(ImGui.CalcTextSize(state).X);
                ImGui.TextColored(isLoaded ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Muted, state);
                DrawMutedLine(companion.Purpose);
            }
        }

        EndCard();
    }

    private bool[] ProbeCompanions()
    {
        var plugins = pluginInterface.InstalledPlugins;
        return Companions
            .Select(companion => plugins.Any(plugin =>
                plugin.IsLoaded &&
                string.Equals(plugin.InternalName, companion.InternalName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private void RegisterLast(string id, string label, Action invoke) =>
        reviewRegistry.RegisterLastItem(id, label, AgentBridgeUiControlKind.Button, true, false, null, invoke);

    /// <summary>The tallest measured content among cards sharing a row, or the estimate before any was measured.</summary>
    private float CardHeight(float estimate, params string[] ids)
    {
        var measured = 0f;
        foreach (var id in ids)
        {
            if (measuredCardHeights.TryGetValue(id, out var height))
                measured = MathF.Max(measured, height);
        }

        return measured > 0f ? measured : estimate;
    }

    private bool BeginCard(string id, float height)
    {
        currentCardId = id;
        currentCardMeasured = false;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Palette.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, Palette.Border);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
        var visible = ImGui.BeginChild(id, new Vector2(0f, height), true);
        // A clipped child draws nothing; measuring it would shrink the card to its padding.
        currentCardMeasured = !visible;
        return visible;
    }

    private void EndCard()
    {
        if (!currentCardMeasured)
            RecordCardHeight(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().WindowPadding.Y);
        currentCardId = null;
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    /// <summary>Pins the card's buttons to its bottom edge when the facts leave room; otherwise they follow the facts.</summary>
    private void BeginCardFooter(float cardHeight)
    {
        RecordCardHeight(ImGui.GetCursorPosY() + ImGui.GetFrameHeight() + ImGui.GetStyle().WindowPadding.Y);
        var footerY = cardHeight - ImGui.GetFrameHeight() - ImGui.GetStyle().WindowPadding.Y;
        if (ImGui.GetCursorPosY() < footerY)
            ImGui.SetCursorPosY(footerY);
    }

    /// <summary>Content height measured before the footer is pinned, so the pin never feeds back into the size.</summary>
    private void RecordCardHeight(float contentHeight)
    {
        if (currentCardId is null)
            return;

        // Two pixels of slack keep the bordered child from showing a scrollbar on rounding.
        measuredCardHeights[currentCardId] = MathF.Ceiling(contentHeight) + 2f;
        currentCardMeasured = true;
    }

    private static void DrawCardTitle(string title, string pill, DalamudUiTone tone)
    {
        ImGui.TextColored(TextColor, title);
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 2f);
        DalamudUiChrome.DrawBadge(pill, Palette, tone);
    }

    private static void DrawStatusLine(Vector4 color, string text)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var position = ImGui.GetCursorScreenPos();
        var radius = MathF.Max(2f, lineHeight * 0.22f);
        ImGui.GetWindowDrawList().AddCircleFilled(
            new Vector2(position.X + radius, position.Y + (lineHeight * 0.5f)),
            radius,
            ImGui.GetColorU32(color));
        ImGui.Dummy(new Vector2(radius * 2f, lineHeight));
        ImGui.SameLine();
        ImGui.TextColored(color, text);
    }

    private static void DrawSubheading(string text) =>
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, text.ToUpperInvariant());

    private static void DrawDescription(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, DescriptionColor);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void DrawMutedLine(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, MarketMafiosoUiTheme.Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void DrawFact(string text, Vector4? color = null) =>
        DrawFactParts((text, color ?? TextColor));

    private static void DrawFactParts(params (string Text, Vector4 Color)[] parts)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "•");
        foreach (var (text, color) in parts)
        {
            if (string.IsNullOrEmpty(text))
                continue;
            ImGui.SameLine(0f, text.StartsWith(' ') ? 0f : -1f);
            ImGui.TextColored(color, text);
        }
    }

    private static bool SecondaryButton(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered]);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(2);
        return clicked;
    }

    private static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2);

    private static string DescribeNames(IReadOnlyList<string> names, int total)
    {
        var shown = names.Take(2).ToArray();
        var text = string.Join(", ", shown);
        return total > shown.Length ? $"{text} +{total - shown.Length:N0} more" : text;
    }

    internal static string DescribeAge(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at.ToUniversalTime();
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    private static readonly HomeSnapshot EmptySnapshot = new(
        new HomeWorkshopFacts(0, 0, [], 0, 0, false, false, string.Empty, 0, 0),
        new HomeTradeFacts(0, 0, null, false, false, string.Empty, 0),
        new HomeMarketFacts(false, false, false, 0, 0, false, string.Empty, 0, 0, null, null, null, null, false),
        new HomeHostFacts(HomeHostConnection.NotConnected, string.Empty, null, false, string.Empty, null, false, 0),
        null);

    private sealed record CompanionPlugin(string DisplayName, string InternalName, string Purpose);
}
