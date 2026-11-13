using System;
using Dalamud.Game.ClientState.Conditions;

namespace MarketMafioso.MarketDiagnostics;

/// <summary>Game and MMF state sampled at the moment a relog is requested.</summary>
internal sealed record MarketRelogGameState
{
    public bool LoggedIn { get; init; }
    public string Name { get; init; } = string.Empty;
    public string HomeWorld { get; init; } = string.Empty;
    public uint? CurrentDataCenterId { get; init; }
    public uint? HomeDataCenterId { get; init; }
    public Func<ConditionFlag, bool> IsConditionSet { get; init; } = static _ => false;
    public bool RetainerSessionOpen { get; init; }

    /// <summary>Why MMF's own automation cannot be interrupted, or null when it is idle.</summary>
    public string? AutomationBusyReason { get; init; }
}

/// <summary>Decides whether a user-requested relog may start. Pure so it is testable without a client.</summary>
internal static class MarketRelogPolicy
{
    internal const string WrongDataCenterReason = "Return to your home data center to relog.";

    private static readonly (ConditionFlag[] Flags, string Reason)[] ConditionGuards =
    [
        ([ConditionFlag.LoggingOut], "The character is already logging out."),
        ([ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51], "The character is between areas."),
        ([ConditionFlag.InCombat], "The character is in combat."),
        ([ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95], "The character is bound by a duty."),
        ([ConditionFlag.Crafting, ConditionFlag.PreparingToCraft, ConditionFlag.ExecutingCraftingAction], "The character is crafting."),
        ([ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78], "A cutscene is playing."),
        ([ConditionFlag.OccupiedSummoningBell], "A retainer session is open."),
        ([ConditionFlag.OccupiedInQuestEvent], "The character is in a quest event."),
        ([ConditionFlag.OccupiedInEvent], "The character is occupied in an event."),
    ];

    /// <summary>Returns why a relog must be refused right now, or null when it may start.</summary>
    public static string? GetRefusal(
        bool lockoutActive,
        bool relogInFlight,
        bool browseActive,
        MarketRelogGameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!lockoutActive)
            return "No market-board lockout is active.";
        if (relogInFlight)
            return "A relog is already in progress.";
        if (!state.LoggedIn || string.IsNullOrWhiteSpace(state.Name) || string.IsNullOrWhiteSpace(state.HomeWorld))
            return "No logged-in character is available to relog.";
        if (browseActive)
            return "A market-board search is in progress.";
        if (!string.IsNullOrWhiteSpace(state.AutomationBusyReason))
            return state.AutomationBusyReason;

        foreach (var (flags, reason) in ConditionGuards)
        {
            foreach (var flag in flags)
            {
                if (state.IsConditionSet(flag))
                    return reason;
            }
        }

        if (state.RetainerSessionOpen)
            return "A retainer session is open.";

        // The relog returns the character to its home world; refusing off the home data center avoids an
        // unexpected data-center transfer. An unknown data center is refused too rather than guessed.
        if (state.CurrentDataCenterId is not { } current || state.HomeDataCenterId is not { } home)
            return "The current or home data center is unknown.";

        return current == home ? null : WrongDataCenterReason;
    }
}
