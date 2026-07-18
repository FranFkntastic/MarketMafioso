using System;

namespace MarketMafioso.Squire.Observation;

public enum RenderedRetainerUiPreparationStatus
{
    Idle,
    Traveling,
    TargetingBell,
    ApproachingBell,
    WaitingForRetainerList,
    Complete,
    Failed,
    Cancelled,
}

public sealed record RenderedRetainerUiPreparationProgress(
    RenderedRetainerUiPreparationStatus Status,
    int InteractionAttempts,
    string Diagnostic);

/// <summary>
/// Pure coordinator for a semantic UI workflow. External adapters may issue normal game commands
/// and observe Lifestream progress, but completion is authorized only by the rendered RetainerList.
/// </summary>
public sealed class RenderedRetainerUiPreparationCoordinator
{
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TravelSettleWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetainerListWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BellApproachTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BellApproachSettleWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BellTargetWait = TimeSpan.FromSeconds(2);
    private const int MaxInteractionAttempts = 3;

    private RenderedRetainerUiPreparationStatus status = RenderedRetainerUiPreparationStatus.Idle;
    private DateTimeOffset phaseStartedAt;
    private int interactionAttempts;
    private string diagnostic = "Retainer UI preparation has not started.";

    public RenderedRetainerUiPreparationProgress Begin(
        DateTimeOffset nowUtc,
        bool retainerListVisible,
        bool lifestreamAvailable,
        Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);
        interactionAttempts = 0;
        if (retainerListVisible)
            return Complete("The rendered Retainer List is already visible.");
        if (!lifestreamAvailable)
            return Fail("Lifestream is unavailable, so the bridge cannot prepare retainer observation without foreground control.");
        if (!processCommand("/li mb"))
            return Fail("Lifestream did not accept the semantic market-board travel command.");

        status = RenderedRetainerUiPreparationStatus.Traveling;
        phaseStartedAt = nowUtc;
        diagnostic = "Lifestream market-board travel requested; waiting without taking window focus.";
        return Snapshot();
    }

    public RenderedRetainerUiPreparationProgress Advance(
        DateTimeOffset nowUtc,
        bool retainerListVisible,
        bool lifestreamStateAvailable,
        bool lifestreamBusy,
        bool marketBoardUiVisible,
        bool bellTargetVisible,
        bool vnavmeshReady,
        bool vnavmeshRunning,
        string localizedBellName,
        Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);
        if (retainerListVisible)
            return Complete("The rendered Retainer List is visible and ready for evidence capture.");
        if (status is RenderedRetainerUiPreparationStatus.Complete or RenderedRetainerUiPreparationStatus.Failed or RenderedRetainerUiPreparationStatus.Cancelled)
            return Snapshot();

        switch (status)
        {
            case RenderedRetainerUiPreparationStatus.Traveling:
                if (!lifestreamStateAvailable)
                    return Fail("Lifestream travel began, but its bounded busy state is unavailable.");
                if (nowUtc - phaseStartedAt > TravelTimeout)
                    return Fail("Lifestream did not finish market-board travel within five minutes.");
                // Lifestream remains busy while the market-board addon it opened is visible.
                // That rendered addon is stronger arrival evidence than the plugin's busy flag.
                if ((lifestreamBusy && !marketBoardUiVisible) || nowUtc - phaseStartedAt < TravelSettleWindow)
                    return Snapshot();
                if (string.IsNullOrWhiteSpace(localizedBellName) ||
                    !processCommand($"rendered-ui:click-nameplate:{localizedBellName.Replace(":", string.Empty, StringComparison.Ordinal)}"))
                    return Fail("The localized Summoning Bell rendered nameplate action was unavailable.");
                status = RenderedRetainerUiPreparationStatus.TargetingBell;
                phaseStartedAt = nowUtc;
                diagnostic = "Summoning Bell target requested through the normal command UI.";
                return Snapshot();

            case RenderedRetainerUiPreparationStatus.TargetingBell:
                if (!bellTargetVisible)
                {
                    if (nowUtc - phaseStartedAt < BellTargetWait)
                        return Snapshot();
                    return Fail("The rendered target bar did not confirm the localized Summoning Bell target.");
                }
                if (!vnavmeshReady)
                    return Fail("vnavmesh is unavailable, so the bridge cannot approach the UI-targeted Summoning Bell without foreground control.");
                if (!processCommand("/vnav movetarget"))
                    return Fail("vnavmesh did not accept movement toward the UI-targeted Summoning Bell.");
                status = RenderedRetainerUiPreparationStatus.ApproachingBell;
                phaseStartedAt = nowUtc;
                diagnostic = "Approaching the UI-targeted Summoning Bell without taking window focus.";
                return Snapshot();

            case RenderedRetainerUiPreparationStatus.ApproachingBell:
                if (nowUtc - phaseStartedAt > BellApproachTimeout)
                    return Fail("vnavmesh did not finish approaching the UI-targeted Summoning Bell within thirty seconds.");
                if (vnavmeshRunning || nowUtc - phaseStartedAt < BellApproachSettleWindow)
                    return Snapshot();
                if (!processCommand("/confirm"))
                    return Fail("The normal confirm command was unavailable after approaching the targeted Summoning Bell.");
                interactionAttempts++;
                status = RenderedRetainerUiPreparationStatus.WaitingForRetainerList;
                phaseStartedAt = nowUtc;
                diagnostic = "Summoning Bell interaction requested; waiting for the rendered Retainer List.";
                return Snapshot();

            case RenderedRetainerUiPreparationStatus.WaitingForRetainerList:
                if (nowUtc - phaseStartedAt < RetainerListWait)
                    return Snapshot();
                if (interactionAttempts >= MaxInteractionAttempts)
                    return Fail("The rendered Retainer List did not appear after three bounded Summoning Bell interactions.");
                if (string.IsNullOrWhiteSpace(localizedBellName) ||
                    !processCommand($"rendered-ui:click-nameplate:{localizedBellName.Replace(":", string.Empty, StringComparison.Ordinal)}"))
                    return Fail("The bridge could not reacquire the localized Summoning Bell through its rendered nameplate.");
                status = RenderedRetainerUiPreparationStatus.TargetingBell;
                phaseStartedAt = nowUtc;
                diagnostic = "Retrying the bounded Summoning Bell interaction because no rendered Retainer List appeared.";
                return Snapshot();

            default:
                return Fail("Retainer UI preparation is not active.");
        }
    }

    public RenderedRetainerUiPreparationProgress Cancel()
    {
        if (status is RenderedRetainerUiPreparationStatus.Complete or RenderedRetainerUiPreparationStatus.Failed)
            return Snapshot();
        status = RenderedRetainerUiPreparationStatus.Cancelled;
        diagnostic = "Retainer UI preparation was cancelled; no further commands will be issued.";
        return Snapshot();
    }

    public RenderedRetainerUiPreparationProgress Snapshot() => new(status, interactionAttempts, diagnostic);

    private RenderedRetainerUiPreparationProgress Complete(string message)
    {
        status = RenderedRetainerUiPreparationStatus.Complete;
        diagnostic = message;
        return Snapshot();
    }

    private RenderedRetainerUiPreparationProgress Fail(string message)
    {
        status = RenderedRetainerUiPreparationStatus.Failed;
        diagnostic = message;
        return Snapshot();
    }
}
