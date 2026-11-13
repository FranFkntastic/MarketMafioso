using System;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.ImGuiNotification.EventArgs;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.ProfileLogin;

namespace MarketMafioso.MarketDiagnostics;

public sealed record MarketRelogRequestResult(bool Accepted, string Message);

/// <summary>Retainer-listing refresh and market-lockout controls shown in Settings.</summary>
public interface IRetainerListingRefreshControls
{
    MarketBoardLockoutStatus Lockout { get; }
    int WaitingCount { get; }
    bool ForceRetry(uint itemId);
    MarketRelogRequestResult RequestRelog(string source);

    /// <summary>Why the last relog request was refused, while that cause still holds.</summary>
    string? RelogRefusal { get; }
    string DescribeLockout(MarketBoardLockoutStatus lockout);
}

/// <summary>
/// Projects the market-board lockout and blocked refresh work onto the server info bar and a persistent
/// notification, and owns the user-requested relog. MMF never relogs on its own.
/// </summary>
internal sealed class MarketLockoutAttention : IRetainerListingRefreshControls, IDisposable
{
    private const string DtrTitle = "MarketMafioso Market";
    private static readonly TimeSpan RelogCompletionWindow = TimeSpan.FromMinutes(3);

    private readonly IDtrBar dtrBar;
    private readonly INotificationManager notifications;
    private readonly IMarketBoardBrowseRuntime browseRuntime;
    private readonly RetainerListingRefreshCoordinator refresh;
    private readonly ICharacterLoginDriver loginDriver;
    private readonly Func<MarketRelogGameState> gameState;
    private readonly Func<uint, string?> resolveItemName;
    private readonly Action openRefreshSettings;
    private readonly Action<string> log;

    private IDtrBarEntry? dtrEntry;
    private string? dtrKey;
    private IActiveNotification? notification;
    private string? notificationContent;
    private DateTimeOffset? dismissedLockoutAtUtc;
    private bool lockoutWasActive;
    private DateTimeOffset? relogInFlightUntilUtc;
    private string? lastRelogRefusal;

    public MarketLockoutAttention(
        IDtrBar dtrBar,
        INotificationManager notifications,
        IMarketBoardBrowseRuntime browseRuntime,
        RetainerListingRefreshCoordinator refresh,
        ICharacterLoginDriver loginDriver,
        Func<MarketRelogGameState> gameState,
        Func<uint, string?> resolveItemName,
        Action openRefreshSettings,
        Action<string> log)
    {
        this.dtrBar = dtrBar ?? throw new ArgumentNullException(nameof(dtrBar));
        this.notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        this.browseRuntime = browseRuntime ?? throw new ArgumentNullException(nameof(browseRuntime));
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        this.loginDriver = loginDriver ?? throw new ArgumentNullException(nameof(loginDriver));
        this.gameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        this.resolveItemName = resolveItemName ?? throw new ArgumentNullException(nameof(resolveItemName));
        this.openRefreshSettings = openRefreshSettings ?? throw new ArgumentNullException(nameof(openRefreshSettings));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public MarketBoardLockoutStatus Lockout => browseRuntime.Lockout;

    public int WaitingCount => refresh.PendingCount;

    public bool ForceRetry(uint itemId) => refresh.ForceRetry(itemId);

    public string? RelogRefusal => lastRelogRefusal;

    /// <summary>Runs on the framework thread.</summary>
    public void Tick(DateTimeOffset nowUtc)
    {
        var lockout = browseRuntime.Lockout;
        var waiting = refresh.IsEnabled ? refresh.PendingCount : 0;
        var blocked = refresh.IsEnabled ? refresh.BlockedCount : 0;

        if (relogInFlightUntilUtc is { } until && (!lockout.Active || nowUtc >= until))
            relogInFlightUntilUtc = null;
        if (!lockout.Active)
            lastRelogRefusal = null;
        else if (lastRelogRefusal is not null)
            lastRelogRefusal = GetRelogRefusal(SampleGameState(logFailure: false)); // drop or update a stale refusal

        UpdateServerInfoBar(lockout, waiting, blocked);
        UpdateNotification(lockout, waiting);
        lockoutWasActive = lockout.Active;
    }

    public MarketRelogRequestResult RequestRelog(string source)
    {
        var result = EvaluateAndSubmitRelog();
        if (result.Accepted)
        {
            lastRelogRefusal = null;
            log($"User relog request from {source} accepted: {result.Message}");
        }
        else
        {
            lastRelogRefusal = result.Message;
            log($"User relog request from {source} refused: {result.Message}");
        }

        notificationContent = null;
        return result;
    }

    public string DescribeLockout(MarketBoardLockoutStatus lockout)
    {
        var at = lockout.LockedAtUtc is { } lockedAt
            ? lockedAt.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)
            : "an unknown time";
        var owner = FormatOwner(lockout.TriggerOwner);
        var item = lockout.TriggerItemId == 0
            ? null
            : resolveItemName(lockout.TriggerItemId) ?? $"item {lockout.TriggerItemId}";
        var world = string.IsNullOrWhiteSpace(lockout.TriggerWorld) ? null : lockout.TriggerWorld;
        var subject = (item, world) switch
        {
            (not null, not null) => $"{item} on {world}",
            (not null, null) => item,
            (null, not null) => world,
            _ => null,
        };
        return subject is null
            ? $"Locked out at {at} by {owner}."
            : $"Locked out at {at} by {owner} ({subject}).";
    }

    private MarketRelogGameState SampleGameState(bool logFailure = true)
    {
        try
        {
            return gameState();
        }
        catch (Exception exception)
        {
            if (logFailure)
                log($"Relog readiness could not be sampled: {exception.Message}");
            return new MarketRelogGameState();
        }
    }

    private string? GetRelogRefusal(MarketRelogGameState state) =>
        MarketRelogPolicy.GetRefusal(
            browseRuntime.Lockout.Active,
            relogInFlightUntilUtc is not null,
            browseRuntime.Snapshot.IsActive,
            state);

    private MarketRelogRequestResult EvaluateAndSubmitRelog()
    {
        // Sampled at click time: the character's state may have changed since the button was drawn.
        var who = SampleGameState();
        var refusal = GetRelogRefusal(who);
        if (refusal is not null)
            return new(false, refusal);

        // Lifestream's character change logs out and selects the named character again, which for the current
        // character is exactly a relog.
        var submission = loginDriver.TryBegin(who.Name, who.HomeWorld, changeCharacter: true);
        if (!submission.Success)
            return new(false, string.IsNullOrWhiteSpace(submission.Message) ? submission.Code : submission.Message);

        relogInFlightUntilUtc = DateTimeOffset.UtcNow + RelogCompletionWindow;
        return new(true, $"Relogging {who.Name} ({who.HomeWorld}).");
    }

    private void UpdateServerInfoBar(MarketBoardLockoutStatus lockout, int waiting, int blocked)
    {
        string? text = null;
        string? tooltip = null;
        if (lockout.Active)
        {
            text = "MMF: Market locked";
            tooltip = $"{DescribeLockout(lockout)}\n{waiting} refresh item(s) waiting.";
            if (blocked > 0)
                tooltip += $"\n{blocked} refresh item(s) blocked.";
            tooltip += relogInFlightUntilUtc is not null
                ? "\nRelogging..."
                : lastRelogRefusal is { } refusal
                    ? $"\nRelog refused: {refusal}"
                    : string.Empty;
            tooltip += "\nClick to open MMF settings, where you can relog.";
        }
        else if (blocked > 0)
        {
            text = $"MMF: {blocked} refresh blocked";
            tooltip = "Click to review blocked retainer listing refreshes.";
        }

        var key = text is null ? null : $"{text}\u0001{tooltip}";
        if (string.Equals(key, dtrKey, StringComparison.Ordinal))
            return;

        dtrKey = key;
        if (text is null)
        {
            if (dtrEntry is not null)
                dtrEntry.Shown = false;
            return;
        }

        dtrEntry ??= dtrBar.Get(DtrTitle);
        dtrEntry.Text = text;
        dtrEntry.Tooltip = tooltip ?? string.Empty;
        // Any mouse button fires OnClick, so the entry only navigates; relogging needs a deliberate button press.
        dtrEntry.OnClick = _ => openRefreshSettings();
        dtrEntry.Shown = true;
    }

    private void UpdateNotification(MarketBoardLockoutStatus lockout, int waiting)
    {
        if (!lockout.Active)
        {
            if (notification is not null)
            {
                notification.DismissNow();
                notification = null;
                notificationContent = null;
            }
            if (lockoutWasActive)
            {
                notifications.AddNotification(new Notification
                {
                    Title = "MMF market access restored",
                    Content = waiting == 0
                        ? "Relog observed."
                        : $"Relog observed. {waiting} retainer listing refresh(es) resumed.",
                    Type = NotificationType.Success,
                    InitialDuration = TimeSpan.FromSeconds(8),
                    Minimized = false,
                });
            }
            dismissedLockoutAtUtc = null;
            return;
        }

        if (notification is null && dismissedLockoutAtUtc == lockout.LockedAtUtc)
            return;

        var content = $"{DescribeLockout(lockout)}\n{waiting} retainer listing refresh(es) waiting for relog.";
        if (relogInFlightUntilUtc is not null)
            content += "\nRelogging...";
        else if (lastRelogRefusal is { } refusal)
            content += $"\nRelog refused: {refusal}";

        if (notification is null)
        {
            notification = notifications.AddNotification(new Notification
            {
                Title = "MMF market board locked out",
                Content = content,
                Type = NotificationType.Warning,
                InitialDuration = TimeSpan.MaxValue,
                HardExpiry = DateTime.MaxValue,
                ExtensionDurationSinceLastInterest = TimeSpan.Zero,
                ShowIndeterminateIfNoExpiry = false,
                Minimized = false,
                UserDismissable = true,
            });
            notification.DrawActions += DrawNotificationActions;
            notification.Dismiss += OnNotificationDismissed;
            notificationContent = content;
            return;
        }

        if (!string.Equals(content, notificationContent, StringComparison.Ordinal))
        {
            notification.Content = content;
            notificationContent = content;
        }
    }

    private void DrawNotificationActions(INotificationDrawArgs args)
    {
        var inFlight = relogInFlightUntilUtc is not null;
        if (inFlight)
            ImGui.BeginDisabled();
        if (ImGui.Button("Relog"))
            RequestRelog("notification");
        if (inFlight)
            ImGui.EndDisabled();
    }

    private void OnNotificationDismissed(INotificationDismissArgs args)
    {
        if (!ReferenceEquals(args.Notification, notification))
            return;

        if (args.Reason == NotificationDismissReason.Manual)
            dismissedLockoutAtUtc = browseRuntime.Lockout.LockedAtUtc;
        notification = null;
        notificationContent = null;
    }

    private static string FormatOwner(string? owner) => owner switch
    {
        nameof(MarketBoardBrowseOwner.RetainerListingRefresh) => "retainer listing refresh",
        nameof(MarketBoardBrowseOwner.MarketAcquisition) => "market acquisition",
        nameof(MarketBoardBrowseOwner.MarketListingAcquisition) => "market listing acquisition",
        nameof(MarketBoardBrowseOwner.RemoteAccessProbe) => "remote market probe",
        null or "" => "an MMF market search",
        _ => owner,
    };

    public void Dispose()
    {
        notification?.DismissNow();
        notification = null;
        if (dtrEntry is not null)
        {
            dtrEntry.Remove();
            dtrEntry = null;
        }
    }
}
